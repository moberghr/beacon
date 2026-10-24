using System.Security.Claims;
using Beacon.Core.Authorization;
using Beacon.Core.BackgroundTasks;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Npgsql;
using NUnit.Framework;

namespace Beacon.Tests.Unit.BackgroundTasks;

/// <summary>
/// A background task's life over the real service and EF Core, without a database (<see cref="SqlCapture"/>, §4.7): the
/// start gives up an abandoned task, records a queued one and hands it to the job runner for the caller; a second start on
/// the same subject is refused by the unique index; the run claims only a queued task and records how it finished, with a
/// failure code and never a message; a host without a job runner runs the task in the request. The starts run on the SQL
/// Server provider: the Npgsql insert batch reads only its own data reader, which the capture cannot stand in for.
/// </summary>
[TestFixture]
public class BackgroundTaskServiceTests
{
    private const int TaskId = 42;
    private const int SubjectId = 9;
    private const int RequestedBy = 5;
    private const string CallerHubId = "ext-7";

    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Start_RecordsAQueuedTask_HandsItToTheJobRunnerForTheCaller_AndAnswersWithItsJob()
    {
        var capture = SqlCapture.OnSqlServer()
            .ThenRowsAffected(0)
            .ThenRow(TaskId)
            .ThenRowsAffected(1);
        var scheduler = new Mock<IBeaconScheduler>();
        scheduler.Setup(x => x.EnqueueBackgroundTask(TaskId, CallerHubId)).ReturnsAsync("job-1");
        var executor = new FakeExecutor();

        var ticket = await Service(capture, scheduler.Object, executor).StartAsync(BackgroundTaskKind.MetadataRefresh, SubjectId, CancellationToken.None);

        ticket.Should().Be(new BackgroundTaskTicket(TaskId, "job-1", BackgroundTaskState.Queued));
        capture.Commands.Should().HaveCount(3);
        capture.Commands[1].Should().Contain("INSERT INTO [beacon].[BackgroundTasks]");
        capture.CommandParameters[1].Values.Should().Contain(
            [(int)BackgroundTaskKind.MetadataRefresh, SubjectId, RequestedBy, (int)BackgroundTaskState.Queued]);
        capture.Commands[2].Should().Contain("UPDATE [b]");
        capture.Commands[2].Should().Contain("[b].[JobId] = @");
        capture.CommandParameters[2].Values.Should().Contain("job-1");
        executor.Runs.Should().BeEmpty("the job runs it, not the request");
    }

    [Test]
    public async Task Start_GivesUpATaskOfTheKindOnTheSubjectPastItsLimit_First()
    {
        var capture = SqlCapture.OnSqlServer()
            .ThenRowsAffected(1)
            .ThenRow(TaskId)
            .ThenRowsAffected(1);
        var scheduler = new Mock<IBeaconScheduler>();
        scheduler.Setup(x => x.EnqueueBackgroundTask(TaskId, CallerHubId)).ReturnsAsync("job-1");

        await Service(capture, scheduler.Object, new FakeExecutor()).StartAsync(BackgroundTaskKind.MetadataRefresh, SubjectId, CancellationToken.None);

        var abandon = capture.Commands[0];
        abandon.Should().Contain("UPDATE [b]");
        abandon.Should().Contain("[b].[Kind] = @");
        abandon.Should().Contain("[b].[SubjectId] = @");
        abandon.Should().Contain("[b].[State] IN (0, 1)");
        abandon.Should().Contain("COALESCE([b].[StartedTime], [b].[CreatedTime]) < @");
        capture.CommandParameters[0].Values.Should().Contain(
            [BackgroundTaskFailureCodes.Abandoned, Now.UtcDateTime - FakeExecutor.Limit, (int)BackgroundTaskState.Failed]);
    }

    [Test]
    public async Task Start_WhileATaskOfTheKindOnTheSubjectIsQueuedOrRunning_ReturnsNull_AndEnqueuesNothing()
    {
        var capture = SqlCapture.OnSqlServer()
            .ThenRowsAffected(0)
            .ThenThrow(new PostgresException("duplicate key", "ERROR", "ERROR", "23505"));
        var scheduler = new Mock<IBeaconScheduler>();

        var ticket = await Service(capture, scheduler.Object, new FakeExecutor()).StartAsync(BackgroundTaskKind.MetadataRefresh, SubjectId, CancellationToken.None);

        ticket.Should().BeNull();
        scheduler.Verify(x => x.EnqueueBackgroundTask(It.IsAny<int>(), It.IsAny<string?>()), Times.Never);
    }

    [Test]
    public async Task Start_ThatTheJobRunnerRefuses_FailsTheQueuedTask_AndThrows()
    {
        var capture = SqlCapture.OnSqlServer()
            .ThenRowsAffected(0)
            .ThenRow(TaskId)
            .ThenRowsAffected(1);
        var scheduler = new Mock<IBeaconScheduler>();
        scheduler.Setup(x => x.EnqueueBackgroundTask(TaskId, CallerHubId)).ThrowsAsync(new InvalidOperationException("runner down"));

        var start = () => Service(capture, scheduler.Object, new FakeExecutor()).StartAsync(BackgroundTaskKind.MetadataRefresh, SubjectId, CancellationToken.None);

        await start.Should().ThrowAsync<InvalidOperationException>();
        capture.Commands[2].Should().Contain("UPDATE [b]");
        capture.CommandParameters[2].Values.Should().Contain(
            [BackgroundTaskFailureCodes.EnqueueFailed, (int)BackgroundTaskState.Queued, (int)BackgroundTaskState.Failed]);
    }

    [Test]
    public async Task Start_OnAHostWithoutAJobRunner_RunsTheTaskInTheRequest_AndAnswersHowItFinished()
    {
        var capture = SqlCapture.OnSqlServer()
            .ThenRowsAffected(0)
            .ThenRow(TaskId)
            .ThenRow((int)BackgroundTaskKind.MetadataRefresh, SubjectId, RequestedBy)
            .ThenRowsAffected(1)
            .ThenRowsAffected(1)
            .ThenRow(TaskId, DBNull.Value, (int)BackgroundTaskState.Succeeded, Now.UtcDateTime, Now.UtcDateTime, Now.UtcDateTime, DBNull.Value, 2);
        var executor = new FakeExecutor { Outcome = new BackgroundTaskOutcome(2) };

        var ticket = await Service(capture, new Mock<IBeaconScheduler>().Object, executor)
            .StartAsync(BackgroundTaskKind.MetadataRefresh, SubjectId, CancellationToken.None);

        ticket.Should().Be(new BackgroundTaskTicket(TaskId, null, BackgroundTaskState.Succeeded));
        executor.Runs.Should().Equal(new BackgroundTaskContext(TaskId, SubjectId, RequestedBy));
    }

    [Test]
    public async Task Start_OnAHostWithoutAJobRunner_ATaskThatFails_AnswersFailed_WithoutThrowing()
    {
        var capture = SqlCapture.OnSqlServer()
            .ThenRowsAffected(0)
            .ThenRow(TaskId)
            .ThenRow((int)BackgroundTaskKind.MetadataRefresh, SubjectId, RequestedBy)
            .ThenRowsAffected(1)
            .ThenRowsAffected(1)
            .ThenRow(TaskId, DBNull.Value, (int)BackgroundTaskState.Failed, Now.UtcDateTime, Now.UtcDateTime, Now.UtcDateTime, BackgroundTaskFailureCodes.Failed, DBNull.Value);
        var executor = new FakeExecutor { Failure = new InvalidOperationException("scan failed") };

        var ticket = await Service(capture, new Mock<IBeaconScheduler>().Object, executor)
            .StartAsync(BackgroundTaskKind.MetadataRefresh, SubjectId, CancellationToken.None);

        ticket.Should().Be(new BackgroundTaskTicket(TaskId, null, BackgroundTaskState.Failed));
        capture.CommandParameters[4].Values.Should().Contain(BackgroundTaskFailureCodes.Failed);
    }

    [Test]
    public async Task Start_OfAKindWithoutAnExecutor_IsRefused_BeforeAnythingIsWritten()
    {
        var capture = new SqlCapture();

        var start = () => Service(capture, new Mock<IBeaconScheduler>().Object).StartAsync(BackgroundTaskKind.MetadataRefresh, SubjectId, CancellationToken.None);

        await start.Should().ThrowAsync<InvalidOperationException>();
        capture.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task Execute_ClaimsAQueuedTask_RunsIt_AndRecordsItsSuccessWithTheWarningCount()
    {
        var capture = new SqlCapture()
            .ThenRow((int)BackgroundTaskKind.MetadataRefresh, SubjectId, RequestedBy)
            .ThenRowsAffected(1)
            .ThenRowsAffected(1);
        var executor = new FakeExecutor { Outcome = new BackgroundTaskOutcome(3) };

        await Service(capture, new Mock<IBeaconScheduler>().Object, executor).ExecuteAsync(TaskId, CancellationToken.None);

        executor.Runs.Should().Equal(new BackgroundTaskContext(TaskId, SubjectId, RequestedBy));
        var claim = capture.Commands[1];
        claim.Should().Contain("b.state = 0", "only a queued task is claimed");
        capture.CommandParameters[1].Values.Should().Contain([(int)BackgroundTaskState.Running, Now.UtcDateTime]);
        capture.CommandParameters[2].Values.Should().Contain([(int)BackgroundTaskState.Running, (int)BackgroundTaskState.Succeeded, 3]);
    }

    [Test]
    public async Task Execute_ATaskNoLongerQueued_IsNotRun()
    {
        var capture = new SqlCapture()
            .ThenRow((int)BackgroundTaskKind.MetadataRefresh, SubjectId, RequestedBy)
            .ThenRowsAffected(0);
        var executor = new FakeExecutor();

        await Service(capture, new Mock<IBeaconScheduler>().Object, executor).ExecuteAsync(TaskId, CancellationToken.None);

        executor.Runs.Should().BeEmpty();
        capture.Commands.Should().HaveCount(2, "nothing is written after a claim that took no task");
    }

    [Test]
    public async Task Execute_ATaskThatDoesNotExist_IsNotRun()
    {
        var capture = new SqlCapture().ThenNoRows();
        var executor = new FakeExecutor();

        await Service(capture, new Mock<IBeaconScheduler>().Object, executor).ExecuteAsync(TaskId, CancellationToken.None);

        executor.Runs.Should().BeEmpty();
        capture.Commands.Should().ContainSingle();
    }

    [TestCaseSource(nameof(Failures))]
    public async Task Execute_AFailure_IsRecordedWithItsCode_AndThrownAgain(Exception failure, string code)
    {
        var capture = new SqlCapture()
            .ThenRow((int)BackgroundTaskKind.MetadataRefresh, SubjectId, RequestedBy)
            .ThenRowsAffected(1)
            .ThenRowsAffected(1);
        var executor = new FakeExecutor { Failure = failure };

        var execute = () => Service(capture, new Mock<IBeaconScheduler>().Object, executor).ExecuteAsync(TaskId, CancellationToken.None);

        (await execute.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(failure);
        capture.Commands.Should().HaveCount(3);
        capture.CommandParameters[2].Values.Should().Contain([code, (int)BackgroundTaskState.Running, (int)BackgroundTaskState.Failed]);
        capture.CommandParameters[2].Values.OfType<string>().Should().NotContain(failure.Message, "a failure is stored as a code, never its message");
    }

    [Test]
    public async Task GetStatus_Translates()
    {
        var capture = new SqlCapture().ThenNoRows();

        var status = await Service(capture, new Mock<IBeaconScheduler>().Object).GetStatusAsync(BackgroundTaskKind.MetadataRefresh, SubjectId, TaskId, CancellationToken.None);

        status.Should().BeNull();
        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("FROM beacon.background_tasks AS b");
        sql.Should().Contain("b.id = @");
        sql.Should().Contain("b.kind = @");
        sql.Should().Contain("b.subject_id = @");
        sql.Should().Contain("LIMIT 1");
    }

    [Test]
    public async Task GetStatus_Translates_OnSqlServer()
    {
        var capture = SqlCapture.OnSqlServer().ThenNoRows();

        await Service(capture, new Mock<IBeaconScheduler>().Object).GetStatusAsync(BackgroundTaskKind.MetadataRefresh, SubjectId, TaskId, CancellationToken.None);

        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("FROM [beacon].[BackgroundTasks] AS [b]");
        sql.Should().Contain("[b].[Id] = @");
        sql.Should().Contain("[b].[Kind] = @");
        sql.Should().Contain("[b].[SubjectId] = @");
    }

    [Test]
    public void FailureCodes_NameTheKnownRefusals_AndNothingElse()
    {
        BackgroundTaskFailureCodes.Of(new OperationCanceledException()).Should().Be(BackgroundTaskFailureCodes.Cancelled);
        BackgroundTaskFailureCodes.Of(new BeaconRateLimitedException("busy")).Should().Be(BackgroundTaskFailureCodes.Busy);
        BackgroundTaskFailureCodes.Of(new DataSourceDisabledException(SubjectId)).Should().Be(BackgroundTaskFailureCodes.DataSourceDisabled);
        BackgroundTaskFailureCodes.Of(new DataSourceTransportException("tls")).Should().Be(BackgroundTaskFailureCodes.InsecureTransport);
        BackgroundTaskFailureCodes.Of(new BeaconNotFoundException("gone")).Should().Be(BackgroundTaskFailureCodes.NotFound);
        BackgroundTaskFailureCodes.Of(new BeaconForbiddenException("no")).Should().Be(BackgroundTaskFailureCodes.Forbidden);
        BackgroundTaskFailureCodes.Of(new InvalidOperationException("anything")).Should().Be(BackgroundTaskFailureCodes.Failed);
    }

    private static IEnumerable<TestCaseData> Failures()
    {
        yield return new TestCaseData(new DataSourceDisabledException(SubjectId), BackgroundTaskFailureCodes.DataSourceDisabled);
        yield return new TestCaseData(new OperationCanceledException("stopped"), BackgroundTaskFailureCodes.Cancelled);
        yield return new TestCaseData(new InvalidOperationException("connection to host-a failed"), BackgroundTaskFailureCodes.Failed);
    }

    private static BackgroundTaskService Service(SqlCapture capture, IBeaconScheduler scheduler, params IBackgroundTaskExecutor[] executors)
    {
        var actor = new Mock<IBeaconActorAccessor>();
        actor.Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new BeaconActor("ext-ana", false) { BeaconUserId = RequestedBy });
        var principal = new Mock<IBeaconRequestPrincipal>();
        principal.SetupGet(x => x.HasRequest).Returns(true);
        principal.SetupGet(x => x.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, CallerHubId), new Claim(BeaconClaims.UserId, "another-id")],
            "test")));

        return new BackgroundTaskService(
            capture.Factory(),
            executors,
            scheduler,
            actor.Object,
            principal.Object,
            new FakeTimeProvider(Now),
            NullLogger<BackgroundTaskService>.Instance);
    }

    private sealed class FakeExecutor : IBackgroundTaskExecutor
    {
        public static readonly TimeSpan Limit = TimeSpan.FromMinutes(30);

        public List<BackgroundTaskContext> Runs { get; } = [];

        public BackgroundTaskOutcome Outcome { get; init; } = new();

        public Exception? Failure { get; init; }

        public BackgroundTaskKind Kind => BackgroundTaskKind.MetadataRefresh;

        public TimeSpan AbandonAfter => Limit;

        public Task<BackgroundTaskOutcome> ExecuteAsync(BackgroundTaskContext task, CancellationToken cancellationToken)
        {
            Runs.Add(task);

            return Failure == null ? Task.FromResult(Outcome) : Task.FromException<BackgroundTaskOutcome>(Failure);
        }
    }
}

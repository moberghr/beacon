using System.Data;
using System.Text.Json;
using Beacon.Connector.MySql;
using Beacon.Connector.PostgreSql;
using Beacon.Connector.SqlServer;
using Beacon.Core;
using Beacon.Core.Adapters;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.DataMigration;
using Beacon.Core.Data.Enums;
using Beacon.Core.DataMigration;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.DataMigration;
using Beacon.Core.Models.Queries;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Npgsql;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// A migration job runs once at a time: a run of a job that already has a run starting or in progress does not start (the
/// response says so, the route answers 409) before it reads or writes anything, decided in a serializable transaction so
/// two hosts starting the same job cannot both pass; a run row left behind longer than a run can last is closed as
/// abandoned. A run's row is closed whatever happens to it. A run is stopped after the job's timeout
/// (clamped to 1–1,440 minutes). A job's settings that Beacon does not act on are refused when it is created, as is a mode
/// that is not one of the three. Recording doubles, no database (§4.7).
/// </summary>
[TestFixture]
[NonParallelizable] // DbConnectionFactory is a process-wide registry.
public class MigrationRunIntegrityTests
{
    private const int JobId = 1;
    private const int OtherJobId = 2;
    private const int ReadSource = 10;
    private const int WriteTarget = 20;

    private ProjectAccessWorld _world = null!;
    private List<MigrationExecutionHistory> _executions = null!;
    private Mock<IQueryExecutionPreviewService> _preview = null!;
    private RecordingMigrationWriter _writer = null!;
    private FakeTimeProvider _time = null!;
    private RecordingSecurityAudit _audit = null!;
    private int _connectionsOpened;

    [SetUp]
    public void SetUp()
    {
        _world = new ProjectAccessWorld();
        _world.Source(ReadSource);
        _world.Source(WriteTarget).AllowMigrationWrites = true;
        _executions = [];
        _world.Serve(_executions);

        _preview = new Mock<IQueryExecutionPreviewService>();
        _preview
            .Setup(x => x.ExecuteTemporaryQueryPreview(It.IsAny<QueryData>(), It.IsAny<List<ParameterValue>?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionResult { Success = true, FinalResult = Rows(1) });

        _writer = new RecordingMigrationWriter(DatabaseEngineType.PostgreSQL);
        _audit = new RecordingSecurityAudit();
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero));
        _connectionsOpened = 0;
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, _ =>
        {
            _connectionsOpened++;
            return new RecordingDbConnection { Scalar = x => x.Contains("current_schemas", StringComparison.Ordinal) ? "public" : true };
        });
    }

    [TearDown]
    public void RestoreConnectionFactories()
    {
        new BeaconBuilder(new ServiceCollection(), new ConfigurationBuilder().Build())
            .AddPostgreSqlConnector()
            .AddSqlServerConnector()
            .AddMySqlConnector();
    }

    [Test]
    public async Task ARunOfAJobWithARunInProgress_DoesNotStart_AndIsRecordedAsRefused()
    {
        Job();
        Running(JobId, startedMinutesAgo: 5);

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.RunInProgress.Should().BeTrue();
        result.Status.Should().Be(MigrationStatus.Failed);
        result.ExecutionId.Should().Be(0);
        result.ErrorMessage.Should().Be(MigrationService.RunInProgressMessage);
        _preview.Verify(
            x => x.ExecuteTemporaryQueryPreview(It.IsAny<QueryData>(), It.IsAny<List<ParameterValue>?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _world.Saved.OfType<MigrationExecutionHistory>().Should().BeEmpty("a refused run adds no run row");
        _connectionsOpened.Should().Be(0);
        var refused = _audit.Events.Should().ContainSingle().Which;
        refused.Action.Should().Be(SecurityAuditActions.MigrationJobRunEnded);
        refused.Outcome.Should().Be(SecurityAuditOutcomes.Refused);
        refused.DetailsJson.Should().Contain("\"reason\":\"run_in_progress\"");
        _audit.Ways.Should().Equal("recorded");
    }

    [Test]
    public async Task TheCheckAndTheRunsStart_ShareOneSerializableTransaction()
    {
        Job();

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Completed, result.ErrorMessage);
        result.RunInProgress.Should().BeFalse();
        _world.Transactions.TransactionIsolationLevels.Should().Contain(IsolationLevel.Serializable);
        _world.Transactions.Commits.Should().BeGreaterThanOrEqualTo(1);
    }

    // Two hosts that start the same job at once both find no run in progress; the database refuses one of the two
    // transactions. Its claim is tried once more; a second conflict answers the run as one in progress.
    [Test]
    public async Task ASerializationConflictWhileTheRunStarts_IsTriedOnceMore_ThenAnsweredAsARunInProgress()
    {
        Job();
        var audit = new Mock<ISecurityAuditRecorder>();
        audit
            .Setup(x => x.SaveWithEventsAsync(It.IsAny<BeaconContext>(), It.IsAny<Func<IEnumerable<SecurityAuditEntry>>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(SerializationFailure());

        var result = await Service(audit.Object).ExecuteMigrationJob(Run(), CancellationToken.None);

        result.RunInProgress.Should().BeTrue();
        result.ErrorMessage.Should().Be(MigrationService.RunInProgressMessage);
        audit.Verify(
            x => x.SaveWithEventsAsync(It.IsAny<BeaconContext>(), It.IsAny<Func<IEnumerable<SecurityAuditEntry>>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        _connectionsOpened.Should().Be(0);
    }

    [Test]
    public async Task ASerializationConflictWhileTheRunStarts_ThatDoesNotRecur_StartsTheRun()
    {
        Job();
        var saves = 0;
        var audit = new Mock<ISecurityAuditRecorder>();
        audit
            .Setup(x => x.SaveWithEventsAsync(It.IsAny<BeaconContext>(), It.IsAny<Func<IEnumerable<SecurityAuditEntry>>>(), It.IsAny<CancellationToken>()))
            .Returns((BeaconContext context, Func<IEnumerable<SecurityAuditEntry>> _, CancellationToken token) =>
                ++saves == 1 ? Task.FromException(SerializationFailure()) : context.SaveChangesAsync(token));

        var result = await Service(audit.Object).ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Completed, result.ErrorMessage);
        saves.Should().Be(2, "the claim that conflicted is tried once more");
    }

    [Test]
    public async Task ARunInProgressOfAnotherJob_DoesNotBlockTheRun()
    {
        Job();
        Running(OtherJobId, startedMinutesAgo: 1);

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Completed, result.ErrorMessage);
    }

    // A host that stopped during a run leaves its row Running; once the run could no longer be going on (its timeout
    // and a margin have passed) the row blocks nothing, and the next run closes it as abandoned, with an event.
    [Test]
    public async Task ARunRowOlderThanARunCanLast_IsClosedAsAbandoned_AndDoesNotBlockTheRun()
    {
        Job(timeoutMinutes: 30);
        var abandoned = Running(JobId, startedMinutesAgo: 30 + MigrationJobSettings.AbandonedRunMarginMinutes + 1);

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Completed, result.ErrorMessage);
        abandoned.Status.Should().Be(MigrationStatus.Failed);
        abandoned.CompletedAt.Should().Be(_time.GetUtcNow().UtcDateTime);
        abandoned.ErrorMessage.Should().Be(MigrationService.AbandonedRunMessage);
        var closed = _audit.Of(SecurityAuditActions.MigrationJobRunEnded)
            .Where(x => x.DetailsJson!.Contains("\"reason\":\"abandoned\""))
            .Should().ContainSingle().Which;
        closed.Outcome.Should().Be(SecurityAuditOutcomes.Failure);
        closed.DetailsJson.Should().Contain($"\"executionId\":{abandoned.Id}");
    }

    [Test]
    public async Task ARunRowWithinARunsLength_BlocksTheRun_AndStaysRunning()
    {
        Job(timeoutMinutes: 30);
        var running = Running(JobId, startedMinutesAgo: 30 + MigrationJobSettings.AbandonedRunMarginMinutes - 1);

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.RunInProgress.Should().BeTrue();
        running.Status.Should().Be(MigrationStatus.Running);
    }

    [Test]
    public async Task ARunThatOutlastsItsTimeout_IsStopped_AndRecordedAsFailedWithTheReason()
    {
        Job(timeoutMinutes: 2);
        var reading = new TaskCompletionSource();
        _preview
            .Setup(x => x.ExecuteTemporaryQueryPreview(It.IsAny<QueryData>(), It.IsAny<List<ParameterValue>?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async (QueryData _, List<ParameterValue>? _, int _, CancellationToken token) =>
            {
                reading.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return null;
            });

        var run = Service().ExecuteMigrationJob(Run(), CancellationToken.None);
        await reading.Task;
        _time.Advance(TimeSpan.FromMinutes(2));
        var result = await run;

        result.Status.Should().Be(MigrationStatus.Failed);
        result.ErrorMessage.Should().Contain("timed out after 2 minutes");
        _world.Saved.OfType<MigrationExecutionHistory>().Should().ContainSingle()
            .Which.Should().Match<MigrationExecutionHistory>(x => x.Status == MigrationStatus.Failed && x.CompletedAt != null);
        _writer.Calls.Should().BeEmpty();
        var ended = _audit.Of(SecurityAuditActions.MigrationJobRunEnded).Should().ContainSingle().Which;
        ended.Outcome.Should().Be(SecurityAuditOutcomes.Failure);
        ended.DetailsJson.Should().Contain("\"reason\":\"timed_out\"");
    }

    // The timeout passes after the rows are written: the commit is not cancelled, and the run is what the commit did.
    [Test]
    public async Task ATimeoutThatPassesOnceTheRowsAreWritten_DoesNotCancelTheCommit()
    {
        Job(timeoutMinutes: 2);
        var writer = new ClockAdvancingWriter(_time, TimeSpan.FromMinutes(3));

        var result = await Service(writers: MigrationTestWriters.Of(writer)).ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Completed, result.ErrorMessage);
        result.DestinationRowsWritten.Should().Be(1);
    }

    // The run's end cannot be saved with the context it ran on: the row is closed again from a fresh context (only while it
    // is still Running) and the end event recorded on its own, and the answer is still the run's outcome.
    [Test]
    public async Task AFailedClosingSave_ClosesTheRowFromAFreshContext_AndStillAnswersTheRunsOutcome()
    {
        Job();
        _world.ServeWithSaved(_executions);
        var recorded = new List<SecurityAuditEntry>();
        var audit = new Mock<ISecurityAuditRecorder>();
        audit
            .Setup(x => x.SaveWithEventsAsync(It.IsAny<BeaconContext>(), It.IsAny<Func<IEnumerable<SecurityAuditEntry>>>(), It.IsAny<CancellationToken>()))
            .Returns((BeaconContext context, Func<IEnumerable<SecurityAuditEntry>> _, CancellationToken token) => context.SaveChangesAsync(token));
        audit
            .Setup(x => x.StageAsync(It.IsAny<BeaconContext>(), It.IsAny<SecurityAuditEntry>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the save failed"));
        audit
            .Setup(x => x.RecordAsync(It.IsAny<SecurityAuditEntry>(), It.IsAny<CancellationToken>()))
            .Callback((SecurityAuditEntry entry, CancellationToken _) => recorded.Add(entry))
            .Returns(Task.CompletedTask);

        var result = await Service(audit.Object).ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Completed, result.ErrorMessage);
        result.DestinationRowsWritten.Should().Be(1);
        _world.Saved.OfType<MigrationExecutionHistory>().Should().ContainSingle()
            .Which.Should().Match<MigrationExecutionHistory>(x => x.Status == MigrationStatus.Completed && x.CompletedAt != null);
        recorded.Should().ContainSingle().Which.Action.Should().Be(SecurityAuditActions.MigrationJobRunEnded);
    }

    [TestCase(0, 1)]
    [TestCase(-5, 1)]
    [TestCase(30, 30)]
    [TestCase(1440, 1440)]
    [TestCase(100_000, 1440)]
    public void AStoredTimeout_IsClampedToOneToFourteenFortyMinutes(int stored, int minutes)
    {
        MigrationJobSettings.RunTimeout(stored).Should().Be(TimeSpan.FromMinutes(minutes));
    }

    [Test]
    public async Task AStoredModeThatIsNotOneOfTheThree_FailsTheRun_BeforeTheDestinationIsOpened()
    {
        Job(mode: (MigrationMode)99);

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Failed);
        result.ErrorMessage.Should().Contain("mode");
        _connectionsOpened.Should().Be(0);
        _writer.Calls.Should().BeEmpty();
    }

    [TestCase(99, null, 3, 30, true, "*mode*")]
    [TestCase(0, null, 3, 30, true, "*mode*")]
    [TestCase(1, null, 3, 0, true, "*Timeout*1*1440*")]
    [TestCase(1, null, 3, 1441, true, "*Timeout*1*1440*")]
    [TestCase(1, null, 5, 30, true, "*retr*")]
    [TestCase(1, null, 3, 30, false, "*alidation*")]
    [TestCase(1, "every night", 3, 30, true, "*cron*")]
    public async Task CreatingAJob_WithASettingBeaconDoesNotActOn_IsRefused(
        int mode, string? schedule, int maxRetries, int timeoutMinutes, bool validate, string reason)
    {
        _world.Serve(new List<MigrationJob>());

        var response = await Service().CreateMigrationJob(Request((MigrationMode)mode, schedule, maxRetries, timeoutMinutes, validate), CancellationToken.None);

        response.Success.Should().BeFalse();
        response.ValidationResult!.Errors.Should().ContainSingle().Which.Should().Match(reason);
        _world.Saved.OfType<MigrationJob>().Should().BeEmpty();
    }

    [TestCase(1, null, 3, 30)]
    [TestCase(2, "0 2 * * *", 0, 1)]
    [TestCase(3, "", 3, 1440)]
    public async Task CreatingAJob_WithSupportedSettings_StoresIt(int mode, string? schedule, int maxRetries, int timeoutMinutes)
    {
        _world.Serve(new List<MigrationJob>());

        var response = await Service().CreateMigrationJob(Request((MigrationMode)mode, schedule, maxRetries, timeoutMinutes, true), CancellationToken.None);

        response.Success.Should().BeTrue(response.ErrorMessage);
        _world.Saved.OfType<MigrationJob>().Should().ContainSingle()
            .Which.Should().Match<MigrationJob>(x => x.Mode == (MigrationMode)mode && x.TimeoutMinutes == timeoutMinutes);
    }

    private static CreateMigrationJobRequest Request(MigrationMode mode, string? schedule, int maxRetries, int timeoutMinutes, bool validate)
    {
        return new CreateMigrationJobRequest
        {
            Name = "copy",
            Description = "copy rows",
            DataSourceId = ReadSource,
            QueryText = "SELECT id FROM accounts",
            DestinationDataSourceId = WriteTarget,
            DestinationTable = "target",
            Mode = mode,
            Schedule = schedule,
            MaxRetries = maxRetries,
            TimeoutMinutes = timeoutMinutes,
            ValidateBeforeExecution = validate
        };
    }

    private static Exception SerializationFailure() =>
        new DbUpdateException("save failed", new PostgresException("could not serialize access", "ERROR", "ERROR", "40001"));

    private MigrationExecutionHistory Running(int jobId, int startedMinutesAgo)
    {
        var row = new MigrationExecutionHistory
        {
            Id = 500 + _executions.Count,
            MigrationJobId = jobId,
            StartedAt = _time.GetUtcNow().UtcDateTime.AddMinutes(-startedMinutesAgo),
            Status = MigrationStatus.Running,
            ExecutedQuery = "SELECT id FROM accounts"
        };
        _executions.Add(row);

        return row;
    }

    private static QueryResult Rows(int count)
    {
        return new QueryResult
        {
            QueryResults = "",
            TotalRecords = count,
            DataSourceName = "source",
            SqlQuery = "SELECT id FROM accounts",
            SubscriptionName = "",
            SubscriptionId = null,
            AllRecords = Enumerable.Range(1, count)
                .Select(x => (IDictionary<string, object?>)new Dictionary<string, object?> { ["id"] = x })
                .ToList()
        };
    }

    private MigrationService Service(ISecurityAuditRecorder? audit = null, MigrationBulkWriters? writers = null)
    {
        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(x => x.Decrypt(It.IsAny<string>())).Returns("Host=unused;Database=unused");

        return new MigrationService(
            _world.Factory(),
            TestTransport.Resolver(encryption.Object),
            Mock.Of<IQueryService>(),
            _preview.Object,
            Mock.Of<IBeaconUserContext>(),
            writers ?? MigrationTestWriters.Of(_writer),
            NullLogger<MigrationService>.Instance,
            Options.Create(new MigrationOptions { Enabled = true }),
            TestChangeApprovals.Submitter(), TestProjectAccess.Actors(), audit ?? _audit, _time);
    }

    private void Job(MigrationMode mode = MigrationMode.Insert, int timeoutMinutes = 30)
    {
        _world.Serve(new List<MigrationJob>
        {
            new()
            {
                Id = JobId,
                Name = "copy",
                Description = "copy rows",
                DataSourceId = ReadSource,
                DataSource = _world.DataSources.First(x => x.Id == ReadSource),
                DestinationDataSourceId = WriteTarget,
                DestinationDataSource = _world.DataSources.First(x => x.Id == WriteTarget),
                QueryText = JsonSerializer.Serialize(new[] { new { StepOrder = 1, Name = "step 1", SqlValue = "SELECT id FROM accounts", DataSourceId = ReadSource } }),
                DestinationTable = "target",
                Mode = mode,
                TimeoutMinutes = timeoutMinutes,
                IsEnabled = true
            }
        });
    }

    // A PostgreSQL writer whose insert takes longer than the job's timeout (it moves the clock on), then writes its rows.
    private sealed class ClockAdvancingWriter(FakeTimeProvider time, TimeSpan duration) : IMigrationBulkWriter
    {
        public DatabaseEngineType Engine => DatabaseEngineType.PostgreSQL;

        public Task ClearAsync(MigrationWriteTarget target, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> InsertAsync(MigrationWriteTarget target, MigrationRows rows, CancellationToken cancellationToken)
        {
            time.Advance(duration);

            return Task.FromResult(rows.Rows.Count);
        }

        public Task<int> UpsertAsync(MigrationWriteTarget target, MigrationRows rows, IReadOnlyList<string> keyColumns, CancellationToken cancellationToken) =>
            InsertAsync(target, rows, cancellationToken);
    }

    private static ExecuteMigrationJobRequest Run() => new(JobId, IsManualExecution: true, ExecutionContext: "test");
}

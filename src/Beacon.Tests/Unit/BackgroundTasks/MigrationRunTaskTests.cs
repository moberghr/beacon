using System.Security.Claims;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.BackgroundTasks;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.DataMigration;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.DataMigration;
using Beacon.Core.Models;
using Beacon.Core.Models.DataMigration;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.BackgroundTasks;

/// <summary>
/// A manual migration run started as a background task: the start runs the run's demands (deployment lock, project access,
/// write targets, an existing enabled job) and answers with the ticket, refusing a second task of the job; the task runs as
/// the user who started it, checks the MigrationOperator permission and the job's demands again right before the run, and
/// runs the same migration as the run in the request (which keeps its run claim, lock, timeout and history row).
/// </summary>
[TestFixture]
public class MigrationRunTaskTests
{
    private const int Restricted = 2;
    private const int OtherRestricted = 3;
    private const int OpenProject = 1;

    private const int OpenSource = 10;
    private const int RestrictedSource = 20;
    private const int SharedSource = 30;

    private const int AllowedJob = 1;
    private const int WideningJob = 2;
    private const int DisabledJob = 3;

    private static readonly BackgroundTaskTicket Ticket = new(41, "job-9", BackgroundTaskState.Queued);

    private ProjectAccessWorld _world = null!;
    private Mock<IMigrationService> _migrations = null!;
    private Mock<IBackgroundTaskService> _tasks = null!;

    [SetUp]
    public void SetUp()
    {
        _world = new ProjectAccessWorld();
        _world.Project(OpenProject);
        _world.Project(Restricted, requiresMembership: true);
        _world.Project(OtherRestricted, requiresMembership: true);
        _world.Source(OpenSource, OpenProject);
        _world.Source(RestrictedSource, Restricted).AllowMigrationWrites = true;
        _world.Source(SharedSource, Restricted, OtherRestricted);
        _world.DataSources.First(x => x.Id == OpenSource).AllowMigrationWrites = true;
        _world.FunctionalUser(3, "ext-op", RoleService.RoleLevels.Editor, BeaconPermissions.MigrationOperator);
        _world.Member(Restricted, 3, ProjectRole.Editor);
        _world.Member(OtherRestricted, 3, ProjectRole.Viewer);
        _world.Users.Add(new BeaconUser { Id = 1, ExternalId = "ext-root", UserName = "root", IsEnabled = true, IsSuperAdmin = true });
        _world.User(5, "ext-editor");
        _world.Member(Restricted, 5, ProjectRole.Editor);
        _world.Member(OtherRestricted, 5, ProjectRole.Viewer);
        _world.Serve(new List<MigrationJob>
        {
            Job(AllowedJob, SharedSource, RestrictedSource),
            Job(WideningJob, RestrictedSource, OpenSource),
            Job(DisabledJob, SharedSource, RestrictedSource, isEnabled: false)
        });

        _migrations = new Mock<IMigrationService>();
        _migrations
            .Setup(x => x.ExecuteMigrationJob(It.IsAny<ExecuteMigrationJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecuteMigrationJobResponse(5, MigrationStatus.PartialSuccess, SourceRowsRead: 10, DestinationRowsWritten: 8, RowsFailed: 2));

        _tasks = new Mock<IBackgroundTaskService>();
        _tasks
            .Setup(x => x.StartAsync(BackgroundTaskKind.MigrationRun, It.IsAny<int>(), It.IsAny<Func<int, SecurityAuditEntry>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ticket);
    }

    [Test]
    public async Task Start_HandsTheRunToABackgroundTask_AndAnswersWithItsTicket()
    {
        var ticket = await StartAsync("ext-op", AllowedJob);

        ticket.Should().Be(Ticket);
        _tasks.Verify(x => x.StartAsync(BackgroundTaskKind.MigrationRun, AllowedJob, null, It.IsAny<CancellationToken>()), Times.Once);
        _migrations.Verify(x => x.ExecuteMigrationJob(It.IsAny<ExecuteMigrationJobRequest>(), It.IsAny<CancellationToken>()), Times.Never, "the task runs it, not the request");
    }

    [Test]
    public async Task Start_OfAJobTheCallerMayNotRun_IsForbidden_WithoutATask()
    {
        var start = () => StartAsync("ext-op", WideningJob);

        await start.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(MigrationJobAccess.WiderDestinationMessage);
        VerifyNoTaskStarted();
    }

    [Test]
    public async Task Start_WhileTheDeploymentDoesNotRunMigrations_IsRefused_WithoutATask()
    {
        var start = () => StartAsync("ext-op", AllowedJob, migrationsEnabled: false);

        await start.Should().ThrowAsync<BeaconConflictException>().WithMessage(MigrationWriteTargets.DisabledMessage);
        VerifyNoTaskStarted();
    }

    [Test]
    public async Task Start_OfAMissingJob_IsNotFound_AndOfADisabledOne_IsRefused()
    {
        var missing = () => StartAsync("ext-root", 99);
        var disabled = () => StartAsync("ext-op", DisabledJob);

        await missing.Should().ThrowAsync<BeaconNotFoundException>();
        await disabled.Should().ThrowAsync<InvalidOperationException>().WithMessage("Migration job is disabled.");
        VerifyNoTaskStarted();
    }

    [Test]
    public async Task Start_WhileARunTaskOfTheJobIsQueuedOrRunning_IsAConflict()
    {
        _tasks
            .Setup(x => x.StartAsync(BackgroundTaskKind.MigrationRun, AllowedJob, It.IsAny<Func<int, SecurityAuditEntry>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BackgroundTaskTicket?)null);

        var start = () => StartAsync("ext-op", AllowedJob);

        await start.Should().ThrowAsync<BeaconConflictException>().WithMessage(MigrationService.RunInProgressMessage);
    }

    [Test]
    public async Task TheTask_AsTheUserWhoStartedIt_RunsTheMigration_AndReportsTheRowsThatFailed()
    {
        var outcome = await ExecuteAsRequesterAsync("ext-op", AllowedJob);

        outcome.SkippedCode.Should().BeNull();
        outcome.WarningCount.Should().Be(2);
        _migrations.Verify(
            x => x.ExecuteMigrationJob(
                It.Is<ExecuteMigrationJobRequest>(y => y.MigrationJobId == AllowedJob && y.IsManualExecution && y.ExecutionContext == MigrationRunTaskExecutor.ExecutionContext),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task TheTask_WhenTheUserNoLongerHoldsTheMigrationOperatorPermission_IsSkipped_WithoutRunning()
    {
        var outcome = await ExecuteAsRequesterAsync("ext-editor", AllowedJob);

        outcome.SkippedCode.Should().Be(BackgroundTaskFailureCodes.Forbidden);
        VerifyNotRun();
    }

    [Test]
    public async Task TheTask_WhenTheUserMayNoLongerRunTheJob_IsSkipped_WithoutRunning()
    {
        var outcome = await ExecuteAsRequesterAsync("ext-op", WideningJob);

        outcome.SkippedCode.Should().Be(BackgroundTaskFailureCodes.Forbidden);
        VerifyNotRun();
    }

    [Test]
    public async Task TheTask_WhenTheUserNoLongerExistsAsAStoredUser_IsSkipped_WithoutRunning()
    {
        var outcome = await ExecuteAsRequesterAsync("ext-gone", AllowedJob);

        outcome.SkippedCode.Should().Be(BackgroundTaskFailureCodes.Forbidden);
        VerifyNotRun();
    }

    [Test]
    public async Task TheTask_AsTheSystem_ForACallerWithoutAStoredUser_Runs()
    {
        await using var provider = Services(TestRequestPrincipal.OffRequest(), migrationsEnabled: true);
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISystemWorkScope>().MarkAsSystemWork();

        var outcome = await Executor(scope.ServiceProvider).ExecuteAsync(new BackgroundTaskContext(41, AllowedJob, null), CancellationToken.None);

        outcome.SkippedCode.Should().BeNull();
        _migrations.Verify(x => x.ExecuteMigrationJob(It.IsAny<ExecuteMigrationJobRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task TheTask_InTheRequest_ChecksTheCallersPermission()
    {
        await using var provider = Services(Request("ext-editor"), migrationsEnabled: true);
        await using var scope = provider.CreateAsyncScope();

        var outcome = await Executor(scope.ServiceProvider).ExecuteAsync(new BackgroundTaskContext(41, AllowedJob, 5), CancellationToken.None);

        outcome.SkippedCode.Should().Be(BackgroundTaskFailureCodes.Forbidden);
        VerifyNotRun();
    }

    [Test]
    public async Task TheTask_WhileAnotherRunOfTheJobIsInProgress_IsSkippedAsBusy()
    {
        _migrations
            .Setup(x => x.ExecuteMigrationJob(It.IsAny<ExecuteMigrationJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecuteMigrationJobResponse(0, MigrationStatus.Failed, ErrorMessage: MigrationService.RunInProgressMessage) { RunInProgress = true });

        var outcome = await ExecuteAsRequesterAsync("ext-op", AllowedJob);

        outcome.SkippedCode.Should().Be(BackgroundTaskFailureCodes.Busy);
    }

    [TestCase(5, BackgroundTaskFailureCodes.RunFailed, Description = "the run's history row says why")]
    [TestCase(0, BackgroundTaskFailureCodes.Failed, Description = "refused before a history row existed")]
    public async Task TheTask_WhoseRunFailed_Fails_WithItsCode(int executionId, string code)
    {
        _migrations
            .Setup(x => x.ExecuteMigrationJob(It.IsAny<ExecuteMigrationJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecuteMigrationJobResponse(executionId, MigrationStatus.Failed, ErrorMessage: "failed"));

        var run = () => ExecuteAsRequesterAsync("ext-op", AllowedJob);

        var thrown = await run.Should().ThrowAsync<BackgroundTaskFailedException>();
        thrown.Which.FailureCode.Should().Be(code);
        BackgroundTaskFailureCodes.Of(thrown.Which).Should().Be(code);
    }

    [Test]
    public async Task TheTask_WhileTheDeploymentDoesNotRunMigrations_RunsNothing()
    {
        await using var provider = Services(TestRequestPrincipal.OffRequest(), migrationsEnabled: false);
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IRunAsScope>().RunAs("ext-op");

        var run = () => Executor(scope.ServiceProvider).ExecuteAsync(new BackgroundTaskContext(41, AllowedJob, 3), CancellationToken.None);

        await run.Should().ThrowAsync<BeaconConflictException>();
        VerifyNotRun();
    }

    [Test]
    public void TheTask_IsTheMigrationRunKind_GivenUpOnlyAfterTheLongestRunCanLast()
    {
        var executor = new MigrationRunTaskExecutor(_migrations.Object, null!, null!, null!, null!, TimeProvider.System);

        executor.Kind.Should().Be(BackgroundTaskKind.MigrationRun);
        executor.AbandonAfter.Should().BeGreaterThan(TimeSpan.FromMinutes(1440));
    }

    [TestCase(MigrationStatus.Completed, -120, true, Description = "its run closed")]
    [TestCase(MigrationStatus.Failed, -120, true, Description = "its run failed")]
    [TestCase(MigrationStatus.Completed, -10, false, Description = "its run closed just now: the task may still record it")]
    [TestCase(MigrationStatus.Running, 0, false, Description = "its run goes on")]
    public async Task AStaleTask_IsReconciledWithTheJobsRunHistory(MigrationStatus status, int closedSecondsAgo, bool ended)
    {
        var now = DateTime.UtcNow;
        var taskStarted = now.AddMinutes(-30);
        _world.Serve(new List<MigrationExecutionHistory>
        {
            new()
            {
                Id = 8,
                MigrationJobId = AllowedJob,
                ExecutedQuery = "SELECT 1",
                StartedAt = taskStarted.AddSeconds(5),
                Status = status,
                CompletedAt = status == MigrationStatus.Running ? null : now.AddSeconds(closedSecondsAgo)
            }
        });

        var hasEnded = await Executor().HasEndedAsync(AllowedJob, taskStarted, CancellationToken.None);

        hasEnded.Should().Be(ended);
    }

    [Test]
    public async Task AStaleTask_WhoseRunWasLeftRunningPastTheJobsTimeout_HasEnded()
    {
        var now = DateTime.UtcNow;
        _world.Serve(new List<MigrationExecutionHistory>
        {
            new() { Id = 8, MigrationJobId = AllowedJob, StartedAt = now.AddHours(-3), Status = MigrationStatus.Running, ExecutedQuery = "SELECT 1" }
        });

        var hasEnded = await Executor().HasEndedAsync(AllowedJob, now.AddHours(-3).AddSeconds(-5), CancellationToken.None);

        hasEnded.Should().BeTrue("the job's 30-minute timeout and margin are long past");
    }

    [TestCase(-10, true, Description = "it never reached the run")]
    [TestCase(-1, false, Description = "it may still be checking before the run")]
    public async Task AStaleTask_WithoutARunOfItsOwn_HasEndedOnlyAfterTheClaimGrace(int startedMinutesAgo, bool ended)
    {
        _world.Serve(new List<MigrationExecutionHistory>());

        var hasEnded = await Executor().HasEndedAsync(AllowedJob, DateTime.UtcNow.AddMinutes(startedMinutesAgo), CancellationToken.None);

        hasEnded.Should().Be(ended);
    }

    [Test]
    public async Task AStaleTask_OfAJobThatNoLongerExists_HasEnded()
    {
        _world.Serve(new List<MigrationExecutionHistory>());

        var hasEnded = await Executor().HasEndedAsync(99, DateTime.UtcNow, CancellationToken.None);

        hasEnded.Should().BeTrue();
    }

    [Test]
    public async Task Status_IsReadForTheJobsRunKind_WithTheRunsDemands()
    {
        var status = new BackgroundTaskStatus(41, "job-9", BackgroundTaskState.Running, DateTime.UtcNow, DateTime.UtcNow, null, null, null);
        _tasks
            .Setup(x => x.GetStatusAsync(BackgroundTaskKind.MigrationRun, AllowedJob, 41, It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);
        await using var provider = Services(Request("ext-op"), migrationsEnabled: true);
        await using var scope = provider.CreateAsyncScope();
        var handler = new GetMigrationJobRunHandler(_tasks.Object, scope.ServiceProvider.GetRequiredService<MigrationJobAccess>());

        var read = await handler.Handle(new GetMigrationJobRunQuery(AllowedJob, 41), CancellationToken.None);
        var refused = () => handler.Handle(new GetMigrationJobRunQuery(WideningJob, 41), CancellationToken.None);

        read.Should().BeSameAs(status);
        await refused.Should().ThrowAsync<BeaconForbiddenException>();
    }

    private static TestRequestPrincipal Request(string externalId)
        => TestRequestPrincipal.For(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, externalId)], "Cookies")));

    private async Task<BackgroundTaskTicket> StartAsync(string externalId, int jobId, bool migrationsEnabled = true)
    {
        await using var provider = Services(Request(externalId), migrationsEnabled);
        await using var scope = provider.CreateAsyncScope();
        var handler = new StartMigrationJobRunHandler(
            _tasks.Object,
            _world.Factory(),
            scope.ServiceProvider.GetRequiredService<MigrationJobAccess>(),
            scope.ServiceProvider.GetRequiredService<MigrationWritePolicy>());

        return await handler.Handle(new StartMigrationJobRunCommand(jobId), CancellationToken.None);
    }

    // The job's scope: no request, run as the user who started the task, as the background task service sets it.
    private async Task<BackgroundTaskOutcome> ExecuteAsRequesterAsync(string externalId, int jobId)
    {
        await using var provider = Services(TestRequestPrincipal.OffRequest(), migrationsEnabled: true);
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IRunAsScope>().RunAs(externalId);

        return await Executor(scope.ServiceProvider).ExecuteAsync(new BackgroundTaskContext(41, jobId, 3), CancellationToken.None);
    }

    private MigrationRunTaskExecutor Executor()
        => new(_migrations.Object, null!, null!, null!, _world.Factory(), TimeProvider.System);

    private MigrationRunTaskExecutor Executor(IServiceProvider services)
        => new(
            _migrations.Object,
            services.GetRequiredService<MigrationJobAccess>(),
            services.GetRequiredService<MigrationWritePolicy>(),
            services.GetRequiredService<BackgroundTaskRequesterPermissions>(),
            _world.Factory(),
            TimeProvider.System);

    private ServiceProvider Services(TestRequestPrincipal principal, bool migrationsEnabled)
    {
        var services = new ServiceCollection();
        _world.Register(services);
        services.AddSingleton<IBeaconRequestPrincipal>(principal);
        services.AddScoped<IProjectResourceResolver, ProjectResourceResolver>();
        services.AddScoped<ProjectRequestAuthorizer>();
        services.AddScoped<MigrationJobAccess>();
        services.AddSingleton(Options.Create(new MigrationOptions { Enabled = migrationsEnabled }));
        services.AddScoped<MigrationWritePolicy>();
        services.AddScoped<BackgroundTaskRequesterPermissions>();

        return services.BuildServiceProvider();
    }

    private void VerifyNoTaskStarted()
        => _tasks.Verify(x => x.StartAsync(It.IsAny<BackgroundTaskKind>(), It.IsAny<int>(), It.IsAny<Func<int, SecurityAuditEntry>?>(), It.IsAny<CancellationToken>()), Times.Never);

    private void VerifyNotRun()
        => _migrations.Verify(x => x.ExecuteMigrationJob(It.IsAny<ExecuteMigrationJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);

    private MigrationJob Job(int id, int source, int destination, bool isEnabled = true)
        => new()
        {
            Id = id,
            Name = $"job {id}",
            Description = "copy",
            DataSourceId = source,
            DataSource = _world.DataSources.First(x => x.Id == source),
            DestinationDataSourceId = destination,
            DestinationDataSource = _world.DataSources.First(x => x.Id == destination),
            QueryText = "SELECT 1",
            DestinationTable = "target",
            IsEnabled = isEnabled
        };
}

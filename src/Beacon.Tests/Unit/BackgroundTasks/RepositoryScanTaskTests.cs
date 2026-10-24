using Beacon.AI.Handlers.Projects;
using Beacon.AI.Services.GitHub;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.BackgroundTasks;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Models;
using Beacon.Core.SecurityAudit;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.BackgroundTasks;

/// <summary>
/// A scan of a project's repositories started as a background task: the start is on record with the repository ids and its
/// task and answers with the ticket, refusing a second scan task of the project; the task runs as the user who started it,
/// checks again that the user may change the project, scans each of the project's repositories (one failing does not stop
/// the others) and reports how many failed or hit a limit.
/// </summary>
[TestFixture]
public class RepositoryScanTaskTests
{
    private const int ProjectId = 4;

    private static readonly BackgroundTaskTicket Ticket = new(51, "job-3", BackgroundTaskState.Queued);

    private ProjectAccessWorld _world = null!;
    private List<GitHubRepository> _repositories = null!;
    private Mock<IGitHubScannerService> _scanner = null!;
    private Mock<IBackgroundTaskHeartbeat> _heartbeat = null!;
    private FakeTimeProvider _time = null!;
    private RecordingSecurityAudit _audit = null!;

    [SetUp]
    public void SetUp()
    {
        _world = new ProjectAccessWorld();
        _world.Project(ProjectId, requiresMembership: true);
        _world.Project(5, requiresMembership: true);
        _world.User(3, "ext-editor");
        _world.User(6, "ext-viewer");
        _world.Member(ProjectId, 3, ProjectRole.Editor);
        _world.Member(ProjectId, 6, ProjectRole.Viewer);
        _repositories =
        [
            new() { Id = 12, ProjectId = ProjectId, RepositoryUrl = "https://github.com/org/finance-etl" },
            new() { Id = 9, ProjectId = ProjectId, RepositoryUrl = "https://github.com/org/finance" },
            new() { Id = 30, ProjectId = 5, RepositoryUrl = "https://github.com/org/other" }
        ];
        _world.Serve(_repositories);
        _scanner = new Mock<IGitHubScannerService>();
        _heartbeat = new Mock<IBackgroundTaskHeartbeat>();
        _heartbeat.Setup(x => x.BeatAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        _audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(3, "ext-editor"));
    }

    [Test]
    public async Task Start_HandsTheScanToABackgroundTask_OnRecordWithTheRepositoriesAndTheTask()
    {
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(3, "ext-editor"));
        var tasks = Tasks(Ticket, audit);

        var ticket = await StartHandler(tasks.Object).Handle(new StartRepositoryScanCommand(ProjectId), CancellationToken.None);

        ticket.Should().Be(Ticket);
        _scanner.Verify(x => x.ScanRepositoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never, "the task scans, not the request");
        audit.Single("ext-editor", "github.com").Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.RepositoryScanRequested, ActorUserId = (int?)3, ProjectId = (int?)ProjectId, ResourceId = "4", DetailsJson = "{\"repositoryIds\":[9,12],\"taskId\":51}" },
            x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task Start_WhileAScanTaskOfTheProjectIsQueuedOrRunning_IsAConflict_AndNotOnRecord()
    {
        var audit = new RecordingSecurityAudit();

        var start = () => StartHandler(Tasks(null, audit).Object).Handle(new StartRepositoryScanCommand(ProjectId), CancellationToken.None);

        await start.Should().ThrowAsync<BeaconConflictException>().WithMessage(StartRepositoryScanHandler.AlreadyRunningMessage);
        audit.Ways.Should().BeEmpty();
    }

    [Test]
    public async Task TheTask_AsTheUserWhoStartedIt_ScansEachRepositoryOfTheProject_AndCountsTheOnesThatFailed()
    {
        _scanner
            .Setup(x => x.ScanRepositoryAsync(ProjectId, 12, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("unreachable"));

        var outcome = await ExecuteAsRequesterAsync("ext-editor");

        outcome.SkippedCode.Should().BeNull();
        outcome.WarningCount.Should().Be(1);
        _scanner.Verify(x => x.ScanRepositoryAsync(ProjectId, 9, It.IsAny<CancellationToken>()), Times.Once);
        _scanner.Verify(x => x.ScanRepositoryAsync(ProjectId, 12, It.IsAny<CancellationToken>()), Times.Once);
        _scanner.Verify(x => x.ScanRepositoryAsync(It.IsAny<int>(), 30, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task TheTask_CountsTheRepositoriesWhoseScanHitALimit()
    {
        _repositories[1].ScanStatus = ScanStatus.Completed;
        _repositories[1].LastScanError = RepositoryScanNotes.Format(["only the first 3 of 7 files were scanned"]);

        var outcome = await ExecuteAsRequesterAsync("ext-editor");

        outcome.WarningCount.Should().Be(1);
    }

    [Test]
    public async Task TheTask_WhenTheUserMayNoLongerChangeTheProject_IsSkipped_WithoutScanning()
    {
        var outcome = await ExecuteAsRequesterAsync("ext-viewer");

        outcome.SkippedCode.Should().Be(BackgroundTaskFailureCodes.Forbidden);
        _scanner.Verify(x => x.ScanRepositoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public void TheTask_IsTheRepositoryScanKind_GivenUpOnlyPastItsDeadlineAndOneRepositorysTimeout()
    {
        var options = new GitHubScanOptions { TaskDeadline = TimeSpan.FromHours(2), ScanTimeout = TimeSpan.FromMinutes(10) };

        var executor = Executor(null!, options);

        executor.Kind.Should().Be(BackgroundTaskKind.RepositoryScan);
        executor.AbandonAfter.Should().Be(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(10) + RepositoryScanTaskExecutor.AbandonMargin);
        new GitHubScanOptions().TaskDeadline.Should().Be(TimeSpan.FromHours(3));
    }

    [Test]
    public async Task TheTask_StartsNoRepositorysScanPastItsDeadline_AndCountsTheRestAsNotScanned()
    {
        _scanner
            .Setup(x => x.ScanRepositoryAsync(ProjectId, 9, It.IsAny<CancellationToken>()))
            .Callback(() => _time.Advance(TimeSpan.FromHours(4)))
            .Returns(Task.CompletedTask);

        var outcome = await ExecuteAsRequesterAsync("ext-editor");

        outcome.WarningCount.Should().Be(1, "the second repository was not scanned");
        _scanner.Verify(x => x.ScanRepositoryAsync(ProjectId, 12, It.IsAny<CancellationToken>()), Times.Never);
        _audit.Of(SecurityAuditActions.RepositoryScanRan).Should().ContainSingle()
            .Which.DetailsJson.Should().Be("{\"taskId\":51,\"repositoryIds\":[9],\"failedCount\":0,\"partialCount\":0,\"notScannedCount\":1}");
    }

    [Test]
    public async Task TheTask_ShowsItIsGoingBeforeEachRepository_AndStopsOnceItWasGivenUp()
    {
        _heartbeat.SetupSequence(x => x.BeatAsync(51, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .ReturnsAsync(false);

        var outcome = await ExecuteAsRequesterAsync("ext-editor");

        outcome.WarningCount.Should().Be(1);
        _heartbeat.Verify(x => x.BeatAsync(51, It.IsAny<CancellationToken>()), Times.Exactly(2));
        _scanner.Verify(x => x.ScanRepositoryAsync(ProjectId, 9, It.IsAny<CancellationToken>()), Times.Once);
        _scanner.Verify(x => x.ScanRepositoryAsync(ProjectId, 12, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task TheTask_IsOnRecordWithTheRepositoriesItScannedWhenItRan()
    {
        _repositories.Add(new() { Id = 15, ProjectId = ProjectId, RepositoryUrl = "https://github.com/org/added-after-the-start" });

        await ExecuteAsRequesterAsync("ext-editor");

        _audit.Single("ext-editor", "github.com", "added-after-the-start").Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.RepositoryScanRan, ActorUserId = (int?)3, ProjectId = (int?)ProjectId, ResourceId = "4", DetailsJson = "{\"taskId\":51,\"repositoryIds\":[9,12,15],\"failedCount\":0,\"partialCount\":0,\"notScannedCount\":0}" },
            x => x.ExcludingMissingMembers());
    }

    [Test]
    public void ARefusedTask_IsOnRecordAsARefusedScan()
    {
        var refused = Executor(null!, new GitHubScanOptions()).SkippedEvent(new BackgroundTaskContext(51, ProjectId, 3), BackgroundTaskFailureCodes.Forbidden);

        refused!.Action.Should().Be(SecurityAuditActions.RepositoryScanRan);
        refused.Outcome.Should().Be(SecurityAuditOutcomes.Refused);
        refused.ProjectId.Should().Be(ProjectId);
        refused.Details!.ToJson().Should().Be("{\"taskId\":51,\"reason\":\"forbidden\"}");
    }

    [Test]
    public async Task Status_IsReadForTheProjectsScanKind()
    {
        var status = new BackgroundTaskStatus(51, "job-3", BackgroundTaskState.Succeeded, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, null, 0);
        var tasks = new Mock<IBackgroundTaskService>();
        tasks
            .Setup(x => x.GetStatusAsync(BackgroundTaskKind.RepositoryScan, ProjectId, 51, It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);

        var read = await new GetRepositoryScanHandler(tasks.Object).Handle(new GetRepositoryScanQuery(ProjectId, 51), CancellationToken.None);

        read.Should().BeSameAs(status);
    }

    // The service saves the start's audit event, built from the new task's id, with the task: this one hands it to
    // <paramref name="audit"/> when the start is accepted.
    private static Mock<IBackgroundTaskService> Tasks(BackgroundTaskTicket? ticket, ISecurityAuditRecorder audit)
    {
        var tasks = new Mock<IBackgroundTaskService>();
        tasks
            .Setup(x => x.StartAsync(BackgroundTaskKind.RepositoryScan, ProjectId, It.IsAny<Func<int, SecurityAuditEntry>?>(), It.IsAny<CancellationToken>()))
            .Returns(async (BackgroundTaskKind _, int _, Func<int, SecurityAuditEntry>? auditEvent, CancellationToken cancellationToken) =>
            {
                if (ticket != null && auditEvent != null)
                {
                    await audit.RecordAsync(auditEvent(ticket.TaskId), cancellationToken);
                }

                return ticket;
            });

        return tasks;
    }

    private StartRepositoryScanHandler StartHandler(IBackgroundTaskService tasks)
        => new(tasks, _world.Factory(), TestProjectAccess.Actors(), NullLogger<StartRepositoryScanHandler>.Instance);

    // The job's scope: no request, run as the user who started the task, as the background task service sets it.
    private async Task<BackgroundTaskOutcome> ExecuteAsRequesterAsync(string externalId)
    {
        var services = new ServiceCollection();
        _world.Register(services);
        services.AddSingleton<IBeaconRequestPrincipal>(TestRequestPrincipal.OffRequest());
        services.AddScoped<IProjectResourceResolver, ProjectResourceResolver>();
        services.AddScoped<ProjectRequestAuthorizer>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IRunAsScope>().RunAs(externalId);
        var executor = Executor(scope.ServiceProvider.GetRequiredService<ProjectRequestAuthorizer>(), new GitHubScanOptions());

        return await executor.ExecuteAsync(new BackgroundTaskContext(51, ProjectId, 3), CancellationToken.None);
    }

    private RepositoryScanTaskExecutor Executor(ProjectRequestAuthorizer authorizer, GitHubScanOptions options)
        => new(
            _world.Factory(),
            _scanner.Object,
            authorizer,
            _heartbeat.Object,
            _audit,
            Options.Create(options),
            _time,
            NullLogger<RepositoryScanTaskExecutor>.Instance);
}

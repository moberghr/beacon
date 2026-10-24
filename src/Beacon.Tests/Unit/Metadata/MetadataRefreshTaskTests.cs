using Beacon.Core.Authorization;
using Beacon.Core.BackgroundTasks;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.DataSources;
using Beacon.Core.Models;
using Beacon.Core.Models.Metadata;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Metadata;

/// <summary>
/// A forced metadata refresh run as a background task: the start keeps the refresh's cooldown and its one-at-a-time rule
/// (in this process, and for a task queued or running anywhere), is on record with its task, and answers with the ticket;
/// the task runs the same refresh, reports the names it left out, and starts the cooldown when it succeeds.
/// </summary>
[TestFixture]
public class MetadataRefreshTaskTests
{
    private const int DataSourceId = 7;

    private static readonly BackgroundTaskTicket Ticket = new(31, "job-1", BackgroundTaskState.Queued);

    [Test]
    public async Task Start_HandsTheRefreshToABackgroundTask_AndAnswersWithItsTicket()
    {
        var tasks = Tasks(Ticket);
        var world = new World();

        var ticket = await world.StartHandler(tasks.Object).Handle(new StartDataSourceMetadataRefreshCommand(DataSourceId), CancellationToken.None);

        ticket.Should().Be(Ticket);
        tasks.Verify(x => x.StartAsync(BackgroundTaskKind.MetadataRefresh, DataSourceId, It.IsAny<CancellationToken>()), Times.Once);
        world.Metadata.Verify(x => x.RefreshMetadataAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never, "the task scans, not the request");
    }

    [Test]
    public async Task Start_IsOnRecord_WithItsTask_AndWhetherItSkippedTheCooldown()
    {
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(4, "ext-dsa"));
        var world = new World { Audit = audit };

        await world.StartHandler(Tasks(Ticket).Object).Handle(new StartDataSourceMetadataRefreshCommand(DataSourceId), CancellationToken.None);

        var started = audit.Single("ext-dsa");
        started.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.MetadataRefreshed, ActorUserId = (int?)4, ResourceType = SecurityAuditResources.DataSource, ResourceId = "7" },
            x => x.ExcludingMissingMembers());
        started.DetailsJson.Should().Be("{\"forced\":true,\"cooldownExempt\":false,\"taskId\":31}");
    }

    [Test]
    public async Task Start_WithinTheCooldown_IsRefused_WithoutStartingATask()
    {
        var world = new World();
        await world.Executor().ExecuteAsync(new BackgroundTaskContext(1, DataSourceId, null), CancellationToken.None);
        var tasks = Tasks(Ticket);

        var start = () => world.StartHandler(tasks.Object).Handle(new StartDataSourceMetadataRefreshCommand(DataSourceId), CancellationToken.None);

        await start.Should().ThrowAsync<BeaconRateLimitedException>().WithMessage("*less than 15 minutes ago*");
        tasks.Verify(x => x.StartAsync(It.IsAny<BackgroundTaskKind>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Start_ByADataSourceAdmin_IsNotHeldToTheCooldown()
    {
        var world = new World { Permissions = TestPermissions.Holding(BeaconPermissions.DataSourceAdmin) };
        await world.Executor().ExecuteAsync(new BackgroundTaskContext(1, DataSourceId, null), CancellationToken.None);

        var ticket = await world.StartHandler(Tasks(Ticket).Object).Handle(new StartDataSourceMetadataRefreshCommand(DataSourceId), CancellationToken.None);

        ticket.Should().Be(Ticket);
    }

    [Test]
    public async Task Start_WhileARefreshOfTheSourceRunsInThisProcess_IsRefused()
    {
        var world = new World();
        world.Cooldown.TryStart(DataSourceId, 15, ignoreCooldown: true).Should().BeNull();
        var tasks = Tasks(Ticket);

        var start = () => world.StartHandler(tasks.Object).Handle(new StartDataSourceMetadataRefreshCommand(DataSourceId), CancellationToken.None);

        await start.Should().ThrowAsync<BeaconRateLimitedException>().WithMessage(ForcedMetadataRefreshCooldown.AlreadyRunningMessage);
        tasks.Verify(x => x.StartAsync(It.IsAny<BackgroundTaskKind>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Start_WhileATaskOfTheSourceIsQueuedOrRunning_IsRefused_AndNotOnRecord()
    {
        var audit = new RecordingSecurityAudit();
        var world = new World { Audit = audit };

        var start = () => world.StartHandler(Tasks(null).Object).Handle(new StartDataSourceMetadataRefreshCommand(DataSourceId), CancellationToken.None);

        await start.Should().ThrowAsync<BeaconRateLimitedException>().WithMessage(ForcedMetadataRefreshCooldown.AlreadyRunningMessage);
        audit.Ways.Should().BeEmpty();
    }

    [Test]
    public async Task Start_DoesNotHoldTheRefreshSlot_SoATaskRunInTheRequestCanTakeIt()
    {
        var world = new World();

        await world.StartHandler(Tasks(Ticket).Object).Handle(new StartDataSourceMetadataRefreshCommand(DataSourceId), CancellationToken.None);

        world.Cooldown.TryStart(DataSourceId, 15, ignoreCooldown: true).Should().BeNull();
    }

    [Test]
    public async Task TheTask_RunsTheRefresh_ReportsTheNamesItLeftOut_AndStartsTheCooldown()
    {
        var world = new World { SkippedNames = 2 };

        var outcome = await world.Executor().ExecuteAsync(new BackgroundTaskContext(1, DataSourceId, 4), CancellationToken.None);

        outcome.WarningCount.Should().Be(2);
        world.Metadata.Verify(x => x.RefreshMetadataAsync(DataSourceId, true, It.IsAny<CancellationToken>()), Times.Once);
        world.RelationshipSync.Verify(x => x.SyncAsync(DataSourceId, It.IsAny<CancellationToken>()), Times.Once);
        world.Cooldown.Refusal(DataSourceId, 15, ignoreCooldown: false).Should().Contain("less than 15 minutes ago");
        world.Cooldown.Refusal(DataSourceId, 15, ignoreCooldown: true).Should().BeNull("the refresh no longer runs");
    }

    [Test]
    public async Task TheTask_WhileARefreshOfTheSourceRunsInThisProcess_IsRefusedAsBusy_WithoutScanning()
    {
        var world = new World();
        world.Cooldown.TryStart(DataSourceId, 15, ignoreCooldown: true).Should().BeNull();

        var run = () => world.Executor().ExecuteAsync(new BackgroundTaskContext(1, DataSourceId, null), CancellationToken.None);

        var refused = await run.Should().ThrowAsync<BeaconRateLimitedException>();
        BackgroundTaskFailureCodes.Of(refused.Which).Should().Be(BackgroundTaskFailureCodes.Busy);
        world.Metadata.Verify(x => x.RefreshMetadataAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task TheTask_ThatFails_DoesNotStartTheCooldown_AndFreesTheSource()
    {
        var world = new World();
        world.Metadata
            .Setup(x => x.RefreshMetadataAsync(DataSourceId, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DataSourceDisabledException(DataSourceId));

        var run = () => world.Executor().ExecuteAsync(new BackgroundTaskContext(1, DataSourceId, null), CancellationToken.None);

        await run.Should().ThrowAsync<DataSourceDisabledException>();
        world.Cooldown.Refusal(DataSourceId, 15, ignoreCooldown: false).Should().BeNull();
    }

    [Test]
    public async Task TheTask_ARelationshipSyncFailure_KeepsTheRefresh()
    {
        var world = new World();
        world.RelationshipSync
            .Setup(x => x.SyncAsync(DataSourceId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("sync failed"));

        var outcome = await world.Executor().ExecuteAsync(new BackgroundTaskContext(1, DataSourceId, null), CancellationToken.None);

        outcome.WarningCount.Should().Be(0);
        world.Cooldown.Refusal(DataSourceId, 15, ignoreCooldown: false).Should().NotBeNull("the refresh succeeded");
    }

    [Test]
    public void TheTask_IsTheMetadataRefreshKind_GivenUpAfterAnHour()
    {
        var executor = new World().Executor();

        executor.Kind.Should().Be(BackgroundTaskKind.MetadataRefresh);
        executor.AbandonAfter.Should().Be(TimeSpan.FromHours(1));
    }

    [Test]
    public async Task Status_IsReadForTheDataSourcesRefreshKind()
    {
        var status = new BackgroundTaskStatus(31, "job-1", BackgroundTaskState.Running, DateTime.UtcNow, DateTime.UtcNow, null, null, null);
        var tasks = new Mock<IBackgroundTaskService>();
        tasks
            .Setup(x => x.GetStatusAsync(BackgroundTaskKind.MetadataRefresh, DataSourceId, 31, It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);

        var read = await new GetDataSourceMetadataRefreshHandler(tasks.Object)
            .Handle(new GetDataSourceMetadataRefreshQuery(DataSourceId, 31), CancellationToken.None);

        read.Should().BeSameAs(status);
    }

    private static Mock<IBackgroundTaskService> Tasks(BackgroundTaskTicket? ticket)
    {
        var tasks = new Mock<IBackgroundTaskService>();
        tasks
            .Setup(x => x.StartAsync(BackgroundTaskKind.MetadataRefresh, DataSourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ticket);

        return tasks;
    }

    private sealed class World
    {
        public World()
        {
            Metadata
                .Setup(x => x.RefreshMetadataAsync(It.IsAny<int>(), true, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new DatabaseMetadataSnapshot(DataSourceId, DatabaseEngineType.PostgreSQL, [], DateTime.UtcNow) { SkippedNameCount = SkippedNames });
        }

        public Mock<IDatabaseMetadataService> Metadata { get; } = new();

        public Mock<ISchemaRelationshipSyncService> RelationshipSync { get; } = new();

        public ForcedMetadataRefreshCooldown Cooldown { get; } = new(new MemoryCache(new MemoryCacheOptions()), TimeProvider.System);

        public IBeaconPermissionAccessor Permissions { get; init; } = TestPermissions.None;

        public ISecurityAuditRecorder Audit { get; init; } = new RecordingSecurityAudit();

        public int SkippedNames { get; init; }

        private static IOptionsMonitor<MetadataRefreshOptions> Options =>
            Mock.Of<IOptionsMonitor<MetadataRefreshOptions>>(x => x.CurrentValue == new MetadataRefreshOptions { ForcedRefreshCooldownMinutes = 15 });

        public StartDataSourceMetadataRefreshHandler StartHandler(IBackgroundTaskService tasks)
            => new(tasks, Cooldown, Options, Permissions, Audit);

        public MetadataRefreshTaskExecutor Executor()
            => new(
                new ForcedMetadataRefresh(Metadata.Object, RelationshipSync.Object, NullLogger<ForcedMetadataRefresh>.Instance),
                Cooldown,
                Options);
    }
}

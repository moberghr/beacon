using Beacon.Core.Authorization;
using Beacon.Core.BackgroundTasks;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities;
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
        tasks.Verify(x => x.StartAsync(BackgroundTaskKind.MetadataRefresh, DataSourceId, It.IsNotNull<Func<int, SecurityAuditEntry>?>(), It.IsAny<CancellationToken>()), Times.Once);
        world.Scans.Should().Be(0, "the task scans, not the request");
    }

    [Test]
    public async Task Start_IsOnRecord_WithItsTask_AndWhetherItSkippedTheCooldown()
    {
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(4, "ext-dsa"));
        var world = new World();

        await world.StartHandler(Tasks(Ticket, audit).Object).Handle(new StartDataSourceMetadataRefreshCommand(DataSourceId), CancellationToken.None);

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
        tasks.Verify(x => x.StartAsync(It.IsAny<BackgroundTaskKind>(), It.IsAny<int>(), It.IsAny<Func<int, SecurityAuditEntry>?>(), It.IsAny<CancellationToken>()), Times.Never);
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
        tasks.Verify(x => x.StartAsync(It.IsAny<BackgroundTaskKind>(), It.IsAny<int>(), It.IsAny<Func<int, SecurityAuditEntry>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Start_WhileATaskOfTheSourceIsQueuedOrRunning_IsRefused_AndNotOnRecord()
    {
        var audit = new RecordingSecurityAudit();
        var world = new World();

        var start = () => world.StartHandler(Tasks(null, audit).Object).Handle(new StartDataSourceMetadataRefreshCommand(DataSourceId), CancellationToken.None);

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
        world.Scans.Should().Be(1);
        world.RelationshipSync.Verify(x => x.SyncAsync(DataSourceId, It.IsAny<CancellationToken>()), Times.Once);
        world.Cooldown.Refusal(DataSourceId, 15, ignoreCooldown: false).Should().Contain("less than 15 minutes ago");
        world.Cooldown.Refusal(DataSourceId, 15, ignoreCooldown: true).Should().BeNull("the refresh no longer runs");
    }

    [Test]
    public async Task TheTask_WhileARefreshOfTheSourceRunsInThisProcess_IsSkippedAsBusy_WithoutScanning()
    {
        var world = new World();
        world.Cooldown.TryStart(DataSourceId, 15, ignoreCooldown: true).Should().BeNull();

        var outcome = await world.Executor().ExecuteAsync(new BackgroundTaskContext(1, DataSourceId, null), CancellationToken.None);

        outcome.SkippedCode.Should().Be(BackgroundTaskFailureCodes.Busy);
        world.Scans.Should().Be(0);
    }

    [Test]
    public async Task TheTask_WhenTheUserMayNoLongerManageTheSource_IsSkipped_WithoutScanning()
    {
        var world = new World { MayManage = false };

        var outcome = await world.Executor().ExecuteAsync(new BackgroundTaskContext(1, DataSourceId, 4), CancellationToken.None);

        outcome.SkippedCode.Should().Be(BackgroundTaskFailureCodes.Forbidden);
        world.AccessCheck.Verify(
            x => x.AllowsAsync(ProjectAction.Manage, It.Is<IReadOnlyCollection<int>>(y => y.SequenceEqual(new[] { DataSourceId })), It.IsAny<string>(), 1, It.IsAny<CancellationToken>()),
            Times.Once);
        world.Scans.Should().Be(0);
        world.Cooldown.Refusal(DataSourceId, 15, ignoreCooldown: true).Should().BeNull("a skipped task holds no refresh slot");
    }

    [Test]
    public async Task TheTask_ThatFails_DoesNotStartTheCooldown_AndFreesTheSource()
    {
        var world = new World();
        world.Source.DisabledTime = DateTime.UtcNow;

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

    // The service saves the start's audit event, built from the new task's id, with the task: this one hands it to
    // <paramref name="audit"/> when the start is accepted.
    private static Mock<IBackgroundTaskService> Tasks(BackgroundTaskTicket? ticket, ISecurityAuditRecorder? audit = null)
    {
        var tasks = new Mock<IBackgroundTaskService>();
        tasks
            .Setup(x => x.StartAsync(BackgroundTaskKind.MetadataRefresh, DataSourceId, It.IsAny<Func<int, SecurityAuditEntry>?>(), It.IsAny<CancellationToken>()))
            .Returns(async (BackgroundTaskKind _, int _, Func<int, SecurityAuditEntry>? auditEvent, CancellationToken cancellationToken) =>
            {
                if (ticket != null && audit != null && auditEvent != null)
                {
                    await audit.RecordAsync(auditEvent(ticket.TaskId), cancellationToken);
                }

                return ticket;
            });

        return tasks;
    }

    private sealed class World
    {
        public DataSource Source { get; } = new()
        {
            Id = DataSourceId,
            Name = "warehouse",
            DataSourceType = DataSourceType.Database,
            EncryptedConnectionData = "encrypted",
            DatabaseEngineType = DatabaseEngineType.PostgreSQL
        };

        /// <summary>How many catalog scans the metadata service ran.</summary>
        public int Scans { get; private set; }

        public Mock<ISchemaRelationshipSyncService> RelationshipSync { get; } = new();

        public ForcedMetadataRefreshCooldown Cooldown { get; } = new(new MemoryCache(new MemoryCacheOptions()), TimeProvider.System);

        public IBeaconPermissionAccessor Permissions { get; init; } = TestPermissions.None;

        public Mock<IExecutionAccessCheck> AccessCheck { get; } = new();

        public bool MayManage { get; init; } = true;

        public int SkippedNames { get; init; }

        private static IOptionsMonitor<MetadataRefreshOptions> Options =>
            Mock.Of<IOptionsMonitor<MetadataRefreshOptions>>(x => x.CurrentValue == new MetadataRefreshOptions { ForcedRefreshCooldownMinutes = 15 });

        public StartDataSourceMetadataRefreshHandler StartHandler(IBackgroundTaskService tasks)
            => new(tasks, Cooldown, Options, Permissions);

        public MetadataRefreshTaskExecutor Executor()
        {
            AccessCheck
                .Setup(x => x.AllowsAsync(It.IsAny<ProjectAction>(), It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MayManage);

            return new MetadataRefreshTaskExecutor(
                new ForcedMetadataRefresh(TestMetadataServices.Scanning(Source, Scan), RelationshipSync.Object, NullLogger<ForcedMetadataRefresh>.Instance),
                Cooldown,
                Options,
                AccessCheck.Object);
        }

        // The catalog the scan reads: one table whose name the refresh leaves out per skipped name.
        private Task<IReadOnlyList<TableMetadataDto>> Scan()
        {
            Scans++;

            return Task.FromResult<IReadOnlyList<TableMetadataDto>>(Enumerable.Range(1, SkippedNames)
                .Select(x => new TableMetadataDto("public", $"orders;{x}", [], [], null))
                .ToList());
        }
    }
}

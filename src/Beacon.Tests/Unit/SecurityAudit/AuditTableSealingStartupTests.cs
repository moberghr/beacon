using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Interceptors;
using Beacon.Core.SecurityAudit;
using Beacon.Tests.Common;
using Recorder = Beacon.Core.SecurityAudit.SecurityAuditRecorder;

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// What the host checks for the execution log and settings history seals once it has started: each table's sealing start
/// is recorded once, with the highest row written before it, and not again while an intact one is there; a context without
/// the sealing interceptor records nothing.
/// </summary>
[TestFixture]
public class AuditTableSealingStartupTests
{
    [Test]
    public async Task SealingStarted_IsRecordedOncePerTable_WithTheHighestRowWrittenBeforeIt()
    {
        var world = new World();
        world.Logs.Add(new ManualQueryExecutionLog { Id = 41, QueryText = "SELECT 1", ResultCount = 1, ExecutionTimeMs = 1, Success = true });
        world.Logs.Add(new ManualQueryExecutionLog { Id = 40, QueryText = "SELECT 1", ResultCount = 1, ExecutionTimeMs = 1, Success = true });

        await world.Startup().RunAsync(CancellationToken.None);

        world.Audit.Events.Select(x => (x.Action, x.ActorKind, x.ResourceType)).Should().Equal(
            (SecurityAuditActions.ExecutionLogSealingStarted, SecurityAuditActorKinds.System, SecurityAuditResources.ExecutionLog),
            (SecurityAuditActions.SettingHistorySealingStarted, SecurityAuditActorKinds.System, SecurityAuditResources.Settings));
        world.Audit.Events[0].DetailsJson.Should().Contain("\"fromRowId\":41");
        world.Audit.Events[1].DetailsJson.Should().Contain("\"fromRowId\":0", "the settings history holds no row yet");

        world.Events.Add(Event(1, SecurityAuditActions.ExecutionLogSealingStarted));
        world.Events.Add(Event(2, SecurityAuditActions.SettingHistorySealingStarted));
        await world.Startup().RunAsync(CancellationToken.None);

        world.Audit.Events.Should().HaveCount(2, "an intact sealing-started event is there for each table");
    }

    [Test]
    public async Task AContextWithoutTheSealingInterceptor_RecordsNothing()
    {
        var world = new World { Sealing = false };

        await world.Startup().RunAsync(CancellationToken.None);

        world.Audit.Events.Should().BeEmpty();
    }

    private static SecurityAuditEvent Event(int id, string action)
    {
        var auditEvent = Recorder.ToEvent(
            new SecurityAuditEntry(action)
                .On(SecurityAuditResources.ExecutionLog, null)
                .With(new SecurityAuditDetails().Add("fromRowId", 0)),
            SecurityAuditActor.System,
            new DateTime(2026, 10, 24, 8, 0, 0, DateTimeKind.Utc),
            null,
            SecurityAuditTestKit.Integrity);
        auditEvent.Id = id;

        return auditEvent;
    }

    private sealed class World
    {
        public List<ManualQueryExecutionLog> Logs { get; } = [];

        public List<SecurityAuditEvent> Events { get; } = [];

        public RecordingSecurityAudit Audit { get; } = new(Core.Authorization.BeaconActor.OffRequest);

        public bool Sealing { get; init; } = true;

        public AuditTableSealingStartup Startup()
        {
            var saved = new List<object>();
            IInterceptor[] interceptors = Sealing ? [new SecurityAuditInterceptor(SecurityAuditTestKit.Integrity, NullLoggerFactory.Instance)] : [];
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(
                    new Dictionary<Type, object>
                    {
                        [typeof(ManualQueryExecutionLog)] = RecordingBeaconContext.MemorySet(Logs, saved),
                        [typeof(AppSettingHistory)] = RecordingBeaconContext.MemorySet(new List<AppSettingHistory>(), saved),
                        [typeof(SecurityAuditEvent)] = RecordingBeaconContext.MemorySet(Events, saved)
                    },
                    saved,
                    interceptors: interceptors));

            return new AuditTableSealingStartup(
                factory.Object,
                SecurityAuditTestKit.Integrity,
                Audit,
                FakeSealProbe.Schema(false),
                NullLoggerFactory.Instance.CreateLogger<AuditTableSealingStartup>());
        }
    }
}

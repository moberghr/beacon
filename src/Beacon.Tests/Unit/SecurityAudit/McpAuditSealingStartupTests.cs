using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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
/// What the host checks for the MCP audit seal once it has started: sealing started is recorded once, with the highest
/// row written before it; a context without the sealing interceptor is a Warning and records nothing; missing seal columns
/// are an Error; a verification that has not run for days is a Warning.
/// </summary>
[TestFixture]
public class McpAuditSealingStartupTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task SealingStarted_IsRecordedOnce_WithTheHighestRowWrittenBeforeIt()
    {
        var world = new World();
        world.Rows.Add(new McpAuditLog { Id = 41, Tool = "query" });
        world.Rows.Add(new McpAuditLog { Id = 40, Tool = "query" });

        await world.Startup().RunAsync(CancellationToken.None);

        var started = world.Audit.Events.Should().ContainSingle().Subject;
        started.Action.Should().Be(SecurityAuditActions.McpAuditSealingStarted);
        started.ActorKind.Should().Be(SecurityAuditActorKinds.System);
        started.DetailsJson.Should().Contain("\"fromAuditId\":41");

        world.Events.Add(Event(1, SecurityAuditActions.McpAuditSealingStarted, Now.UtcDateTime.AddHours(-1)));
        await world.Startup().RunAsync(CancellationToken.None);

        world.Audit.Events.Should().ContainSingle("an intact sealing-started event is there");
    }

    [Test]
    public async Task AContextWithoutTheSealingInterceptor_IsAWarning_AndRecordsNothing()
    {
        var world = new World { Sealing = false };

        await world.Startup().RunAsync(CancellationToken.None);

        world.Logs.Entries.Should().Contain(x => x.Level == LogLevel.Warning && x.Message.Contains(nameof(SecurityAuditInterceptor)));
        world.Audit.Events.Should().BeEmpty();
    }

    [Test]
    public async Task MissingSealColumns_AreAnError_AndRecordNothing()
    {
        var world = new World { ColumnsMissing = true };

        await world.Startup().RunAsync(CancellationToken.None);

        world.Logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Error).Which.Message.Should().Contain("seal columns");
        world.Audit.Events.Should().BeEmpty();
    }

    [TestCase(-1, false)]
    [TestCase(-72, true)]
    public async Task AVerificationThatHasNotRunForDays_IsAWarning(int verifiedHoursAgo, bool warned)
    {
        var world = new World();
        world.Rows.Add(new McpAuditLog { Id = 1, Tool = "query" });
        world.Events.Add(Event(1, SecurityAuditActions.McpAuditSealingStarted, Now.UtcDateTime.AddDays(-10)));
        world.Events.Add(Event(2, SecurityAuditActions.McpAuditVerified, Now.UtcDateTime.AddHours(verifiedHoursAgo)));

        await world.Startup().RunAsync(CancellationToken.None);

        world.Logs.Entries.Any(x => x.Level == LogLevel.Warning && x.Message.Contains("not been verified")).Should().Be(warned);
        world.Audit.Events.Should().BeEmpty();
    }

    private static SecurityAuditEvent Event(int id, string action, DateTime time)
    {
        var auditEvent = Recorder.ToEvent(
            new SecurityAuditEntry(action)
                .On(SecurityAuditResources.McpAudit, null)
                .With(new SecurityAuditDetails().Add("fromAuditId", 0).Add("maxAuditId", 0)),
            SecurityAuditActor.System,
            time,
            null,
            SecurityAuditTestKit.Integrity);
        auditEvent.Id = id;

        return auditEvent;
    }

    private sealed class World
    {
        public List<McpAuditLog> Rows { get; } = [];

        public List<SecurityAuditEvent> Events { get; } = [];

        public RecordingSecurityAudit Audit { get; } = new(Core.Authorization.BeaconActor.OffRequest);

        public LogRecorder Logs { get; } = new();

        public bool Sealing { get; init; } = true;

        public bool ColumnsMissing { get; init; }

        public McpAuditSealingStartup Startup()
        {
            var saved = new List<object>();
            IInterceptor[] interceptors = Sealing ? [new SecurityAuditInterceptor(SecurityAuditTestKit.Integrity, NullLoggerFactory.Instance)] : [];
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(
                    new Dictionary<Type, object>
                    {
                        [typeof(McpAuditLog)] = RecordingBeaconContext.MemorySet(Rows, saved),
                        [typeof(SecurityAuditEvent)] = RecordingBeaconContext.MemorySet(Events, saved)
                    },
                    saved,
                    interceptors: interceptors));
            var schema = new Mock<IMcpAuditSealSchema>();
            schema
                .Setup(x => x.SealColumnsMissingAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(ColumnsMissing);

            return new McpAuditSealingStartup(
                factory.Object,
                schema.Object,
                SecurityAuditTestKit.Integrity,
                Audit,
                new FakeTimeProvider(Now),
                LoggerFactory.Create(x => x.AddProvider(Logs)).CreateLogger<McpAuditSealingStartup>());
        }
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Mcp;
using Beacon.Core.SecurityAudit;
using Beacon.Tests.Common;
using Recorder = Beacon.Core.SecurityAudit.SecurityAuditRecorder;

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// The scheduled verification of the MCP audit: it checks every row written since its last intact checkpoint, by id, with
/// its statements; a changed row is reported at Error and recorded; rows written before rows were sealed are unsealed and
/// never fail; a row without a seal after the first sealed one is unsealed within the upgrade grace and changed after it; a
/// sealed row dated in the future is out of range; a forged checkpoint is passed over; a run that read nothing records
/// nothing; the security audit verification runs it.
/// </summary>
[TestFixture]
public class McpAuditVerificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task TheVerification_ChecksEveryNewRow_ReportsAChangedOneAtError_AndRecordsACheckpoint()
    {
        var store = new Store();
        store.Add(Sealed(1, Now.UtcDateTime.AddHours(-3)));
        var changed = store.Add(Sealed(2, Now.UtcDateTime.AddHours(-2)));
        store.Add(Sealed(3, Now.UtcDateTime.AddMinutes(-1)));
        store.Statements.First(x => x.McpAuditLogId == changed.Id).Sql = "SELECT 2";
        var audit = new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest);
        var logs = new LogRecorder();

        var checkpoint = await Verification(store, audit, logs).VerifyNewRowsAsync(CancellationToken.None);

        checkpoint.Should().BeEquivalentTo(new { FromAuditId = 0, MaxAuditId = 3, CheckedCount = 3, ModifiedCount = 1, UnsealedCount = 0, SealedFromAuditId = (int?)1 });
        checkpoint.FailedAuditIds.Should().Equal(2);
        logs.Entries.Should().ContainSingle(x => x.EventId.Id == SecurityAuditLogEvents.McpAuditVerificationFailed.Id)
            .Which.Level.Should().Be(LogLevel.Error);
        logs.Entries.Should().ContainSingle(x => x.EventId.Id == SecurityAuditLogEvents.McpAuditCheckpoint.Id)
            .Which.Message.Should().Contain("MaxAuditId=3").And.Contain("Checked=3").And.Contain("Failed=1");
        var verified = audit.Events.Should().ContainSingle().Subject;
        verified.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.McpAuditVerified, Outcome = SecurityAuditOutcomes.Failure, ActorKind = SecurityAuditActorKinds.System },
            x => x.ExcludingMissingMembers());
        verified.DetailsJson.Should().Contain("\"maxAuditId\":3").And.Contain("\"failedAuditIds\":[2]").And.Contain("\"sealedFromAuditId\":1");
    }

    [Test]
    public async Task RowsWrittenBeforeRowsWereSealed_AreUnsealed_NeverChanged()
    {
        var store = new Store();
        store.Add(Unsealed(1, Now.UtcDateTime.AddDays(-30)));
        store.Add(Unsealed(2, Now.UtcDateTime.AddDays(-20)));
        store.Add(Sealed(3, Now.UtcDateTime.AddHours(-2)));
        var audit = new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest);
        var logs = new LogRecorder();

        var checkpoint = await Verification(store, audit, logs).VerifyNewRowsAsync(CancellationToken.None);

        checkpoint.Should().BeEquivalentTo(new { MaxAuditId = 3, CheckedCount = 3, ModifiedCount = 0, UnsealedCount = 2, SealedFromAuditId = (int?)3 });
        checkpoint.FailedAuditIds.Should().BeEmpty();
        logs.Entries.Should().NotContain(x => x.Level >= LogLevel.Error);
        audit.Events.Single().Outcome.Should().Be(SecurityAuditOutcomes.Success);
        audit.Events.Single().DetailsJson.Should().Contain("\"unsealedCount\":2");
    }

    // A node still running the version before writes rows without a seal while the others seal theirs; once the upgrade
    // grace has passed since the first sealed row, a row without a seal is one whose seal was removed.
    [TestCase(-2, McpAuditVerdicts.Unsealed)]
    [TestCase(-49, SecurityAuditVerdicts.Modified)]
    public async Task ARowWithoutASeal_AfterTheFirstSealedRow_IsUnsealedOnlyWithinTheUpgradeGrace(int sealedHoursAgo, string verdict)
    {
        var store = new Store();
        store.Add(Sealed(1, Now.UtcDateTime.AddHours(sealedHoursAgo)));
        store.Add(Unsealed(2, Now.UtcDateTime.AddHours(-1)));

        var checkpoint = await Verification(store, new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest), new LogRecorder()).VerifyNewRowsAsync(CancellationToken.None);

        checkpoint.UnsealedCount.Should().Be(verdict == McpAuditVerdicts.Unsealed ? 1 : 0);
        checkpoint.ModifiedCount.Should().Be(verdict == SecurityAuditVerdicts.Modified ? 1 : 0);
    }

    [Test]
    public async Task ASealedRowDatedInTheFuture_IsOutOfRange()
    {
        var store = new Store();
        store.Add(Sealed(1, Now.UtcDateTime.AddDays(3)));

        var checkpoint = await Verification(store, new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest), new LogRecorder()).VerifyNewRowsAsync(CancellationToken.None);

        checkpoint.Should().BeEquivalentTo(new { OutOfRangeCount = 1, SealedFromAuditId = (int?)null });
        checkpoint.FailedAuditIds.Should().Equal(1);
    }

    [Test]
    public async Task TheVerification_ContinuesFromItsLastIntactCheckpoint_AndPassesOverAForgedOne()
    {
        var store = new Store();
        var old = store.Add(Sealed(1, Now.UtcDateTime.AddDays(-5)));
        old.UserId = 99;
        store.Add(Sealed(2, Now.UtcDateTime.AddDays(-4)));
        store.Add(Unsealed(3, Now.UtcDateTime.AddHours(-2)));
        store.Add(Sealed(4, Now.UtcDateTime.AddHours(-1)));
        store.Events.Add(Checkpoint(10, maxAuditId: 2, sealedFromAuditId: 1, Now.UtcDateTime.AddDays(-5)));
        var forged = Checkpoint(11, maxAuditId: 4, sealedFromAuditId: 1, Now.UtcDateTime.AddDays(-5));
        forged.Hmac = new string('a', 64);
        store.Events.Add(forged);
        var logs = new LogRecorder();

        var checkpoint = await Verification(store, new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest), logs).VerifyNewRowsAsync(CancellationToken.None);

        checkpoint.FromAuditId.Should().Be(2, "the forged checkpoint naming row 4 is passed over");
        checkpoint.FailedAuditIds.Should().Equal([3, 1], "row 3 has no seal days after the first sealed row; row 1, checked by an earlier run, is checked again");
        checkpoint.RecheckedCount.Should().Be(2);
        checkpoint.SealedFromAuditId.Should().Be(1);
        logs.Entries.Where(x => x.EventId.Id == SecurityAuditLogEvents.McpAuditVerificationFailed.Id).Should().HaveCount(2, "the forged checkpoint, then the run's failures");
    }

    [Test]
    public async Task ARunThatReadsNothing_RecordsNothing_ButStillWritesItsCheckpoint()
    {
        var store = new Store();
        var audit = new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest);
        var logs = new LogRecorder();

        var checkpoint = await Verification(store, audit, logs).VerifyNewRowsAsync(CancellationToken.None);

        checkpoint.CheckedCount.Should().Be(0);
        audit.Events.Should().BeEmpty();
        logs.Entries.Should().ContainSingle(x => x.EventId.Id == SecurityAuditLogEvents.McpAuditCheckpoint.Id);
    }

    [Test]
    public async Task TheSecurityAuditVerification_RunsTheMcpAuditVerification()
    {
        using var cancellation = new CancellationTokenSource();
        var mcpAudit = new Mock<IMcpAuditVerificationService>();
        mcpAudit
            .Setup(x => x.VerifyNewRowsAsync(cancellation.Token))
            .ReturnsAsync(new McpAuditCheckpoint(0, 0, null, 0, 0, 0, 0, 0, []));
        var events = new List<SecurityAuditEvent>();
        var saved = new List<object>();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(new Dictionary<Type, object> { [typeof(SecurityAuditEvent)] = RecordingBeaconContext.MemorySet(events, saved) }, saved));

        await new SecurityAuditVerificationService(
                factory.Object,
                SecurityAuditTestKit.Integrity,
                new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest),
                new SecurityAuditReadGate(TimeProvider.System),
                Options.Create(new SecurityAuditOptions()),
                new FakeTimeProvider(Now),
                NullLoggerFactory.Instance,
                mcpAudit.Object,
                Mock.Of<IAuditTableVerificationService>())
            .VerifyNewEventsAsync(cancellation.Token);

        mcpAudit.Verify(x => x.VerifyNewRowsAsync(cancellation.Token), Times.Once);
    }

    [TestCase(false, "FROM beacon.mcp_audit_logs", "m.id > @", "ORDER BY m.id", "LIMIT ")]
    [TestCase(true, "FROM [beacon].[McpAuditLogs]", "[m].[Id] > @", "ORDER BY [m].[Id]", "TOP(")]
    public async Task TheVerification_ScansById_NeverByTime_Translates(bool sqlServer, params string[] expected)
    {
        var capture = (sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture()).ThenNoRows().ThenNoRows().ThenNoRows();

        await new McpAuditVerificationService(
                capture.Factory(),
                SecurityAuditTestKit.Integrity,
                new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest),
                Options.Create(new McpDeploymentOptions()),
                new FakeTimeProvider(Now),
                NullLoggerFactory.Instance)
            .VerifyNewRowsAsync(CancellationToken.None);

        capture.Commands.Should().HaveCount(3, "the checkpoints, the sealing-started event, then the first batch of the scan");
        var scan = capture.Commands[2];
        foreach (var fragment in expected)
        {
            scan.Should().Contain(fragment);
        }

        scan[scan.IndexOf("WHERE", StringComparison.Ordinal)..].Should().NotContainAny(["created_time", "CreatedTime"], "a row is never selected by the time it carries");
    }

    [Test]
    public async Task ARowChangedAfterItWasChecked_IsFoundByTheRecheck_WhichStartsOverAtTheCheckpoint()
    {
        var store = new Store();
        store.Add(Sealed(1, Now.UtcDateTime.AddDays(-3)));
        var changed = store.Add(Sealed(2, Now.UtcDateTime.AddDays(-3)));
        store.Add(Sealed(3, Now.UtcDateTime.AddDays(-2)));
        store.Events.Add(Checkpoint(10, maxAuditId: 3, sealedFromAuditId: 1, Now.UtcDateTime.AddDays(-3)));
        changed.ErrorMessage = "changed";
        var audit = new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest);
        var logs = new LogRecorder();

        var checkpoint = await Verification(store, audit, logs).VerifyNewRowsAsync(CancellationToken.None);

        checkpoint.Should().BeEquivalentTo(new { CheckedCount = 0, RecheckedCount = 3, RecheckedThroughAuditId = 0, ModifiedCount = 1, MaxAuditId = 3 });
        checkpoint.FailedAuditIds.Should().Equal(2);
        logs.Entries.Should().ContainSingle(x => x.EventId.Id == SecurityAuditLogEvents.McpAuditVerificationFailed.Id);
        audit.Events.Single().Outcome.Should().Be(SecurityAuditOutcomes.Failure);
    }

    [Test]
    public async Task ARowWhoseCorrelationIdWasChanged_AddedOrRemoved_IsChanged_AndOneSealedWithoutItIsIntact()
    {
        var store = new Store();
        store.Add(Sealed(1, Now.UtcDateTime.AddHours(-4)));
        var changed = store.Add(Sealed(2, Now.UtcDateTime.AddHours(-3), new string('a', 32)));
        var removed = store.Add(Sealed(3, Now.UtcDateTime.AddHours(-2), new string('b', 32)));
        var added = store.Add(Sealed(4, Now.UtcDateTime.AddHours(-1)));
        changed.CorrelationId = new string('c', 32);
        removed.CorrelationId = null;
        added.CorrelationId = new string('d', 32);
        var audit = new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest);
        var logs = new LogRecorder();

        var checkpoint = await Verification(store, audit, logs).VerifyNewRowsAsync(CancellationToken.None);

        checkpoint.Should().BeEquivalentTo(new { MaxAuditId = 4, CheckedCount = 4, ModifiedCount = 3, UnsealedCount = 0 });
        checkpoint.FailedAuditIds.Should().Equal(2, 3, 4);
        audit.Events.Single().Outcome.Should().Be(SecurityAuditOutcomes.Failure);
    }

    [Test]
    public async Task TheRecheck_ContinuesAfterTheRowTheLastRunsRecheckReached()
    {
        var store = new Store();
        var changed = store.Add(Sealed(1, Now.UtcDateTime.AddDays(-3)));
        store.Add(Sealed(2, Now.UtcDateTime.AddDays(-3)));
        store.Add(Sealed(3, Now.UtcDateTime.AddDays(-2)));
        store.Events.Add(Checkpoint(10, maxAuditId: 3, sealedFromAuditId: 1, Now.UtcDateTime.AddDays(-3), x => x.Add("recheckedThroughAuditId", 1)));
        changed.ErrorMessage = "changed";

        var checkpoint = await Verification(store, new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest), new LogRecorder()).VerifyNewRowsAsync(CancellationToken.None);

        checkpoint.RecheckedCount.Should().Be(2, "rows 2 and 3; row 1 is reached again in the next cycle");
        checkpoint.ModifiedCount.Should().Be(0);
    }

    // Row 2 was never there; three rows are: a count of four means a checked row was deleted, of two that one was written
    // below the checkpoint.
    [TestCase(4, 1, 0)]
    [TestCase(2, 0, 1)]
    public async Task RowsUpToTheLastCheckpoint_AreCountedAgainstWhatItCounted(int counted, int missing, int added)
    {
        var store = new Store();
        store.Add(Sealed(1, Now.UtcDateTime.AddDays(-3)));
        store.Add(Sealed(3, Now.UtcDateTime.AddDays(-2)));
        store.Add(Sealed(4, Now.UtcDateTime.AddDays(-2)));
        store.Events.Add(Checkpoint(10, maxAuditId: 4, sealedFromAuditId: 1, Now.UtcDateTime.AddDays(-3), x => x.Add("retainedRowCount", counted)));
        var audit = new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest);
        var logs = new LogRecorder();

        var checkpoint = await Verification(store, audit, logs).VerifyNewRowsAsync(CancellationToken.None);

        checkpoint.Should().BeEquivalentTo(new { MissingRowCount = missing, AddedRowCount = added, RetainedRowCount = (int?)3 });
        logs.Entries.Should().ContainSingle(x => x.EventId.Id == SecurityAuditLogEvents.McpAuditVerificationFailed.Id)
            .Which.Level.Should().Be(LogLevel.Error);
        audit.Events.Single().Outcome.Should().Be(SecurityAuditOutcomes.Failure);
        audit.Events.Single().DetailsJson.Should().Contain("\"retainedRowCount\":3");
    }

    // Under a retention, only the rows the purge cannot reach before the next run are counted; once it may have, the count
    // is not compared.
    [TestCase(-1, 1)]
    [TestCase(-3, 0)]
    public async Task UnderARetention_TheRowCountIsComparedOnlyWhileThePurgeCannotHaveReachedIt(int countedDaysAgo, int missing)
    {
        var store = new Store();
        store.Add(Sealed(2, Now.UtcDateTime.AddDays(-5)));
        var retainedFrom = Now.UtcDateTime.AddDays(countedDaysAgo - 30) + McpAuditVerificationService.RowCountMargin;
        store.Events.Add(Checkpoint(10, maxAuditId: 2, sealedFromAuditId: 2, Now.UtcDateTime.AddDays(-5), x => x.Add("retainedRowCount", 2).Add("retainedFrom", retainedFrom)));
        var logs = new LogRecorder();

        var checkpoint = await Verification(store, new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest), logs, retentionDays: 30).VerifyNewRowsAsync(CancellationToken.None);

        checkpoint.MissingRowCount.Should().Be(missing);
        checkpoint.RetainedRowCount.Should().Be(1);
        if (missing == 0)
        {
            logs.Entries.Should().Contain(x => x.Level == LogLevel.Warning && x.Message.Contains("not counted against the last checkpoint"));
        }
    }

    // Sealing started (the event) after row 1: a row without a seal above it is unsealed only when dated, by its own time,
    // within the upgrade grace of that start, whenever the verification runs.
    [TestCase(1, -71, McpAuditVerdicts.Unsealed)]
    [TestCase(2, -71, McpAuditVerdicts.Unsealed)]
    [TestCase(2, -1, SecurityAuditVerdicts.Modified)]
    [TestCase(2, -80, SecurityAuditVerdicts.Modified)]
    public async Task ARowWithoutASeal_IsJudgedByWhenSealingStarted_AndItsOwnTime(int id, int hoursAgo, string verdict)
    {
        var store = new Store();
        store.Add(Unsealed(id, Now.UtcDateTime.AddHours(hoursAgo)));
        store.Events.Add(SealingStarted(5, fromAuditId: 1, Now.UtcDateTime.AddHours(-72)));

        var checkpoint = await Verification(store, new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest), new LogRecorder()).VerifyNewRowsAsync(CancellationToken.None);

        checkpoint.SealingStartedAuditId.Should().Be(1);
        checkpoint.UnsealedCount.Should().Be(verdict == McpAuditVerdicts.Unsealed ? 1 : 0);
        checkpoint.ModifiedCount.Should().Be(verdict == SecurityAuditVerdicts.Modified ? 1 : 0);
    }

    [Test]
    public async Task ARowWithoutASeal_AboveARowSealedPastTheGrace_IsChanged_WhateverTimeItCarries()
    {
        var store = new Store();
        store.Add(Sealed(2, Now.UtcDateTime.AddHours(-70)));
        store.Add(Sealed(3, Now.UtcDateTime.AddHours(-30)));
        store.Add(Unsealed(4, Now.UtcDateTime.AddHours(-70)));
        store.Events.Add(SealingStarted(5, fromAuditId: 1, Now.UtcDateTime.AddHours(-72)));

        var checkpoint = await Verification(store, new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest), new LogRecorder()).VerifyNewRowsAsync(CancellationToken.None);

        checkpoint.GraceClosedAtAuditId.Should().Be(3);
        checkpoint.FailedAuditIds.Should().Equal(4);
    }

    [Test]
    public async Task ARowSealedUnderTheNextVersion_IsReportedAtWarning_NotAsAFailure()
    {
        var store = new Store();
        var next = store.Add(Sealed(1, Now.UtcDateTime.AddHours(-1)));
        next.SealVersion = McpAuditSeal.CurrentVersion + 1;
        var audit = new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest);
        var logs = new LogRecorder();

        var checkpoint = await Verification(store, audit, logs).VerifyNewRowsAsync(CancellationToken.None);

        checkpoint.Should().BeEquivalentTo(new { UnknownVersionCount = 1, ModifiedCount = 0 });
        checkpoint.FailedAuditIds.Should().BeEmpty();
        logs.Entries.Should().NotContain(x => x.Level >= LogLevel.Error);
        logs.Entries.Should().Contain(x => x.Level == LogLevel.Warning && x.Message.Contains("seal version"));
        audit.Events.Single().Outcome.Should().Be(SecurityAuditOutcomes.Success);
    }

    private static IMcpAuditVerificationService Verification(Store store, RecordingSecurityAudit audit, LogRecorder logs, int? retentionDays = null)
    {
        return new McpAuditVerificationService(
            store.Factory(),
            SecurityAuditTestKit.Integrity,
            audit,
            Options.Create(new McpDeploymentOptions { Audit = new McpAuditOptions { RetentionDays = retentionDays } }),
            new FakeTimeProvider(Now),
            LoggerFactory.Create(x => x.AddProvider(logs)));
    }

    private static McpAuditLog Sealed(int id, DateTime createdTime, string? correlationId = null)
    {
        var row = Unsealed(id, createdTime, correlationId);
        McpAuditSeal.Seal(row, SecurityAuditTestKit.Integrity);

        return row;
    }

    private static McpAuditLog Unsealed(int id, DateTime createdTime, string? correlationId = null)
    {
        return new McpAuditLog
        {
            Id = id,
            CreatedTime = createdTime,
            UserId = 3,
            Tool = "query",
            ProjectId = 42,
            ExecutionTimeMs = 10,
            CorrelationId = correlationId,
            Statements =
            [
                new McpAuditStatement { McpAuditLogId = id, CreatedTime = createdTime, ProjectId = 42, Purpose = McpStatementPurposes.Initial, Sql = "SELECT 1", RowCount = 1 }
            ]
        };
    }

    private static SecurityAuditEvent Checkpoint(int id, int maxAuditId, int sealedFromAuditId, DateTime sealedFromTime, Action<SecurityAuditDetails>? more = null)
    {
        var details = new SecurityAuditDetails()
            .Add("scheduled", true)
            .Add("maxAuditId", maxAuditId)
            .Add("sealedFromAuditId", sealedFromAuditId)
            .Add("sealedFromTime", sealedFromTime);
        more?.Invoke(details);

        return SystemEvent(id, SecurityAuditActions.McpAuditVerified, details, Now.UtcDateTime.AddHours(-6));
    }

    private static SecurityAuditEvent SealingStarted(int id, int fromAuditId, DateTime time)
    {
        return SystemEvent(id, SecurityAuditActions.McpAuditSealingStarted, new SecurityAuditDetails().Add("fromAuditId", fromAuditId), time);
    }

    private static SecurityAuditEvent SystemEvent(int id, string action, SecurityAuditDetails details, DateTime time)
    {
        var auditEvent = Recorder.ToEvent(
            new SecurityAuditEntry(action)
                .On(SecurityAuditResources.McpAudit, null)
                .With(details),
            SecurityAuditActor.System,
            time,
            null,
            SecurityAuditTestKit.Integrity);
        auditEvent.Id = id;

        return auditEvent;
    }

    // The stored rows: the audit rows, their statements (stored apart from them, as read back) and the security events.
    private sealed class Store
    {
        public List<McpAuditLog> Rows { get; } = [];

        public List<McpAuditStatement> Statements { get; } = [];

        public List<SecurityAuditEvent> Events { get; } = [];

        public McpAuditLog Add(McpAuditLog row)
        {
            Statements.AddRange(row.Statements);
            row.Statements = [];
            Rows.Add(row);

            return row;
        }

        public IDbContextFactory<BeaconContext> Factory()
        {
            var saved = new List<object>();
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(
                    new Dictionary<Type, object>
                    {
                        [typeof(McpAuditLog)] = RecordingBeaconContext.MemorySet(Rows, saved),
                        [typeof(McpAuditStatement)] = RecordingBeaconContext.MemorySet(Statements, saved),
                        [typeof(SecurityAuditEvent)] = RecordingBeaconContext.MemorySet(Events, saved)
                    },
                    saved));

            return factory.Object;
        }
    }
}

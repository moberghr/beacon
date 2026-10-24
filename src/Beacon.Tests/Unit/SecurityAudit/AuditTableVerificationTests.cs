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
using Beacon.Core.SecurityAudit;
using Beacon.Tests.Common;
using Recorder = Beacon.Core.SecurityAudit.SecurityAuditRecorder;

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// The scheduled verification of the execution log and the settings history: each table's rows written since its last
/// intact checkpoint are checked by id; a changed row is reported at Error and recorded under the table's own action; rows
/// written before rows were sealed are unsealed and never fail, and a row without a seal past the upgrade grace is changed;
/// a row changed after it was checked is found by the re-check; a forged checkpoint is passed over; rows a retention purge
/// deleted past the retention never fail, while a counted row deleted inside it does; the security audit verification
/// runs it.
/// </summary>
[TestFixture]
public class AuditTableVerificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task TheVerification_ChecksBothTables_ReportsAChangedRowAtError_AndRecordsEachTablesCheckpoint()
    {
        var store = new Store();
        store.Logs.Add(SealedLog(1, Now.UtcDateTime.AddHours(-3)));
        var changed = SealedLog(2, Now.UtcDateTime.AddHours(-2));
        store.Logs.Add(changed);
        changed.QueryText = "SELECT 2";
        store.History.Add(SealedHistory(1, Now.UtcDateTime.AddHours(-1)));
        var audit = new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest);
        var logs = new LogRecorder();

        var checkpoints = await Verification(store, audit, logs).VerifyNewRowsAsync(CancellationToken.None);

        checkpoints.Should().HaveCount(2);
        checkpoints[0].Should().BeEquivalentTo(new { Store = "execution_log", MaxRowId = 2, CheckedCount = 2, ModifiedCount = 1, SealedFromRowId = (int?)1 }, x => x.ExcludingMissingMembers());
        checkpoints[0].FailedRowIds.Should().Equal(2);
        checkpoints[1].Should().BeEquivalentTo(new { Store = "setting_history", MaxRowId = 1, CheckedCount = 1, ModifiedCount = 0 }, x => x.ExcludingMissingMembers());
        logs.Entries.Should().ContainSingle(x => x.EventId.Id == SecurityAuditLogEvents.AuditTableVerificationFailed.Id)
            .Which.Level.Should().Be(LogLevel.Error);
        logs.Entries.Where(x => x.EventId.Id == SecurityAuditLogEvents.AuditTableCheckpoint.Id).Should().HaveCount(2);
        audit.Events.Select(x => (x.Action, x.Outcome, x.ResourceType)).Should().Equal(
            (SecurityAuditActions.ExecutionLogVerified, SecurityAuditOutcomes.Failure, SecurityAuditResources.ExecutionLog),
            (SecurityAuditActions.SettingHistoryVerified, SecurityAuditOutcomes.Success, SecurityAuditResources.Settings));
        audit.Events[0].DetailsJson.Should().Contain("\"maxRowId\":2").And.Contain("\"failedRowIds\":[2]");
        foreach (var auditEvent in audit.Events)
        {
            SecurityAuditTestKit.AssertNoPii(auditEvent);
        }
    }

    [Test]
    public async Task RowsWrittenBeforeRowsWereSealed_AreUnsealed_NeverChanged()
    {
        var store = new Store();
        store.History.Add(UnsealedHistory(1, Now.UtcDateTime.AddDays(-30)));
        store.History.Add(UnsealedHistory(2, Now.UtcDateTime.AddDays(-20)));
        store.History.Add(SealedHistory(3, Now.UtcDateTime.AddHours(-2)));
        var logs = new LogRecorder();

        var checkpoint = (await Verification(store, new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest), logs).VerifyNewRowsAsync(CancellationToken.None))[1];

        checkpoint.Should().BeEquivalentTo(new { MaxRowId = 3, CheckedCount = 3, ModifiedCount = 0, UnsealedCount = 2, SealedFromRowId = (int?)3 }, x => x.ExcludingMissingMembers());
        checkpoint.FailedRowIds.Should().BeEmpty();
        logs.Entries.Should().NotContain(x => x.Level >= LogLevel.Error);
    }

    // Sealing started (the event) after row 1: a row without a seal above it is unsealed only when dated, by its own time,
    // within the upgrade grace of that start.
    [TestCase(1, -71, McpAuditVerdicts.Unsealed)]
    [TestCase(2, -71, McpAuditVerdicts.Unsealed)]
    [TestCase(2, -1, SecurityAuditVerdicts.Modified)]
    public async Task ARowWithoutASeal_IsJudgedByWhenSealingStarted_AndItsOwnTime(int id, int hoursAgo, string verdict)
    {
        var store = new Store();
        store.Logs.Add(UnsealedLog(id, Now.UtcDateTime.AddHours(hoursAgo)));
        store.Events.Add(SystemEvent(5, SecurityAuditActions.ExecutionLogSealingStarted, new SecurityAuditDetails().Add("fromRowId", 1), Now.UtcDateTime.AddHours(-72)));

        var checkpoint = (await Verification(store, new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest), new LogRecorder()).VerifyNewRowsAsync(CancellationToken.None))[0];

        checkpoint.SealingStartedRowId.Should().Be(1);
        checkpoint.UnsealedCount.Should().Be(verdict == McpAuditVerdicts.Unsealed ? 1 : 0);
        checkpoint.ModifiedCount.Should().Be(verdict == SecurityAuditVerdicts.Modified ? 1 : 0);
    }

    [Test]
    public async Task ARowChangedAfterItWasChecked_IsFoundByTheRecheck_AndAForgedCheckpointIsPassedOver()
    {
        var store = new Store();
        store.History.Add(SealedHistory(1, Now.UtcDateTime.AddDays(-3)));
        var changed = SealedHistory(2, Now.UtcDateTime.AddDays(-3));
        store.History.Add(changed);
        store.History.Add(SealedHistory(3, Now.UtcDateTime.AddDays(-2)));
        store.Events.Add(Checkpoint(10, SecurityAuditActions.SettingHistoryVerified, maxRowId: 2));
        var forged = Checkpoint(11, SecurityAuditActions.SettingHistoryVerified, maxRowId: 3);
        forged.Hmac = new string('a', 64);
        store.Events.Add(forged);
        changed.NewValue = "changed";
        var logs = new LogRecorder();

        var checkpoint = (await Verification(store, new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest), logs).VerifyNewRowsAsync(CancellationToken.None))[1];

        checkpoint.FromRowId.Should().Be(2, "the forged checkpoint naming row 3 is passed over");
        checkpoint.Should().BeEquivalentTo(new { CheckedCount = 1, RecheckedCount = 2, ModifiedCount = 1, MaxRowId = 3 }, x => x.ExcludingMissingMembers());
        checkpoint.FailedRowIds.Should().Equal(2);
        logs.Entries.Where(x => x.EventId.Id == SecurityAuditLogEvents.AuditTableVerificationFailed.Id).Should().HaveCount(2, "the forged checkpoint, then the run's failure");
    }

    // Row 2 was deleted after the last run counted three rows up to row 3: without a retention that is a missing row.
    [Test]
    public async Task ACountedRowDeleted_IsAMissingRow()
    {
        var store = new Store();
        store.Logs.Add(SealedLog(1, Now.UtcDateTime.AddDays(-3)));
        store.Logs.Add(SealedLog(3, Now.UtcDateTime.AddDays(-2)));
        store.Events.Add(Checkpoint(10, SecurityAuditActions.ExecutionLogVerified, maxRowId: 3, x => x.Add("retainedRowCount", 3)));
        var audit = new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest);

        var checkpoint = (await Verification(store, audit, new LogRecorder()).VerifyNewRowsAsync(CancellationToken.None))[0];

        checkpoint.Should().BeEquivalentTo(new { MissingRowCount = 1, AddedRowCount = 0, RetainedRowCount = (int?)2 }, x => x.ExcludingMissingMembers());
        audit.Events.Single(x => x.Action == SecurityAuditActions.ExecutionLogVerified).Outcome.Should().Be(SecurityAuditOutcomes.Failure);
    }

    // Under a 30-day retention the last run (a day ago) counted only the rows dated inside the retention plus the margin
    // (rows 2 and 3). The purge has since deleted row 1, past the retention: nothing fails. Deleting row 2, counted, fails.
    [TestCase(false, 0)]
    [TestCase(true, 1)]
    public async Task UnderARetention_ARowThePurgeDeletedPastTheRetention_NeverFails(bool deleteCountedRow, int missing)
    {
        var store = new Store();
        store.Logs.Add(SealedLog(2, Now.UtcDateTime.AddDays(-20)));
        if (!deleteCountedRow)
        {
            store.Logs.Add(SealedLog(3, Now.UtcDateTime.AddDays(-10)));
        }

        var retainedFrom = Now.UtcDateTime.AddDays(-1 - 30) + AuditTableVerificationService.RowCountMargin;
        store.Events.Add(Checkpoint(10, SecurityAuditActions.ExecutionLogVerified, maxRowId: 3, x => x.Add("retainedRowCount", 2).Add("retainedFrom", retainedFrom)));
        var logs = new LogRecorder();

        var checkpoint = (await Verification(store, new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest), logs, executionLogRetentionDays: 30).VerifyNewRowsAsync(CancellationToken.None))[0];

        checkpoint.MissingRowCount.Should().Be(missing);
        checkpoint.ModifiedCount.Should().Be(0);
        checkpoint.FailedCount.Should().Be(missing);
    }

    [Test]
    public async Task UnderARetention_ASealedRowOlderThanTheRetention_IsOutOfRange()
    {
        var store = new Store();
        store.Logs.Add(SealedLog(1, Now.UtcDateTime.AddDays(-40)));

        var checkpoint = (await Verification(store, new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest), new LogRecorder(), executionLogRetentionDays: 30).VerifyNewRowsAsync(CancellationToken.None))[0];

        checkpoint.OutOfRangeCount.Should().Be(1);
        checkpoint.FailedRowIds.Should().Equal(1);
    }

    [Test]
    public async Task ARunThatReadsNothing_RecordsNothing_ButStillWritesItsCheckpoints()
    {
        var audit = new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest);
        var logs = new LogRecorder();

        await Verification(new Store(), audit, logs).VerifyNewRowsAsync(CancellationToken.None);

        audit.Events.Should().BeEmpty();
        logs.Entries.Where(x => x.EventId.Id == SecurityAuditLogEvents.AuditTableCheckpoint.Id).Should().HaveCount(2);
    }

    [Test]
    public async Task TheSecurityAuditVerification_RunsTheAuditTableVerification()
    {
        using var cancellation = new CancellationTokenSource();
        var tables = new Mock<IAuditTableVerificationService>();
        tables
            .Setup(x => x.VerifyNewRowsAsync(cancellation.Token))
            .ReturnsAsync([]);
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
                tables.Object)
            .VerifyNewEventsAsync(cancellation.Token);

        tables.Verify(x => x.VerifyNewRowsAsync(cancellation.Token), Times.Once);
    }

    [TestCase(false, "FROM beacon.manual_query_execution_logs", "m.id > @", "ORDER BY m.id", "LIMIT ")]
    [TestCase(true, "FROM [beacon].[ManualQueryExecutionLogs]", "[m].[Id] > @", "ORDER BY [m].[Id]", "TOP(")]
    public async Task TheVerification_ScansById_NeverByTime_Translates(bool sqlServer, params string[] expected)
    {
        var capture = (sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture()).ThenNoRows().ThenNoRows().ThenNoRows();
        var service = new AuditTableVerificationService(
            capture.Factory(),
            SecurityAuditTestKit.Integrity,
            new RecordingSecurityAudit(Core.Authorization.BeaconActor.OffRequest),
            new Retention(null),
            new FakeTimeProvider(Now),
            NullLoggerFactory.Instance);

        await service.VerifyStoreAsync(AuditTableStores.ExecutionLog, CancellationToken.None);

        capture.Commands.Should().HaveCount(3, "the checkpoints, the sealing-started event, then the first batch of the scan");
        var scan = capture.Commands[2];
        foreach (var fragment in expected)
        {
            scan.Should().Contain(fragment);
        }

        scan[scan.IndexOf("WHERE", StringComparison.Ordinal)..].Should().NotContainAny(["created_time", "CreatedTime"], "a row is never selected by the time it carries");
    }

    private static IAuditTableVerificationService Verification(Store store, RecordingSecurityAudit audit, LogRecorder logs, int? executionLogRetentionDays = null)
    {
        return new AuditTableVerificationService(
            store.Factory(),
            SecurityAuditTestKit.Integrity,
            audit,
            new Retention(executionLogRetentionDays),
            new FakeTimeProvider(Now),
            LoggerFactory.Create(x => x.AddProvider(logs)));
    }

    private static ManualQueryExecutionLog SealedLog(int id, DateTime createdTime)
    {
        var row = UnsealedLog(id, createdTime);
        AuditTableSeal.Seal(row, AuditTableStores.ExecutionLog, SecurityAuditTestKit.Integrity);

        return row;
    }

    private static ManualQueryExecutionLog UnsealedLog(int id, DateTime createdTime)
    {
        return new ManualQueryExecutionLog
        {
            Id = id,
            CreatedTime = createdTime,
            QueryText = "SELECT 1",
            ResultCount = 1,
            ExecutionTimeMs = 3.5,
            DataSourceId = 2,
            Success = true
        };
    }

    private static AppSettingHistory SealedHistory(int id, DateTime createdTime)
    {
        var row = UnsealedHistory(id, createdTime);
        AuditTableSeal.Seal(row, AuditTableStores.SettingHistory, SecurityAuditTestKit.Integrity);

        return row;
    }

    private static AppSettingHistory UnsealedHistory(int id, DateTime createdTime)
    {
        return new AppSettingHistory
        {
            Id = id,
            CreatedTime = createdTime,
            SettingKey = "Mcp:MaxRows",
            OldValue = "100",
            NewValue = "200",
            ChangedAt = createdTime
        };
    }

    private static SecurityAuditEvent Checkpoint(int id, string action, int maxRowId, Action<SecurityAuditDetails>? more = null)
    {
        var details = new SecurityAuditDetails()
            .Add("scheduled", true)
            .Add("maxRowId", maxRowId)
            .Add("sealedFromRowId", 1)
            .Add("sealedFromTime", Now.UtcDateTime.AddDays(-3));
        more?.Invoke(details);

        return SystemEvent(id, action, details, Now.UtcDateTime.AddDays(-1));
    }

    private static SecurityAuditEvent SystemEvent(int id, string action, SecurityAuditDetails details, DateTime time)
    {
        var auditEvent = Recorder.ToEvent(
            new SecurityAuditEntry(action)
                .On(SecurityAuditResources.ExecutionLog, null)
                .With(details),
            SecurityAuditActor.System,
            time,
            null,
            SecurityAuditTestKit.Integrity);
        auditEvent.Id = id;

        return auditEvent;
    }

    // The execution log's retention; the settings history is never purged.
    private sealed class Retention(int? executionLogDays) : IAuditTableRetention
    {
        public int? RetentionDaysOf(string store) => store == AuditTableStores.ExecutionLog.Name ? executionLogDays : null;
    }

    // The stored rows: both tables and the security events.
    private sealed class Store
    {
        public List<ManualQueryExecutionLog> Logs { get; } = [];

        public List<AppSettingHistory> History { get; } = [];

        public List<SecurityAuditEvent> Events { get; } = [];

        public IDbContextFactory<BeaconContext> Factory()
        {
            var saved = new List<object>();
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(
                    new Dictionary<Type, object>
                    {
                        [typeof(ManualQueryExecutionLog)] = RecordingBeaconContext.MemorySet(Logs, saved),
                        [typeof(AppSettingHistory)] = RecordingBeaconContext.MemorySet(History, saved),
                        [typeof(SecurityAuditEvent)] = RecordingBeaconContext.MemorySet(Events, saved)
                    },
                    saved));

            return factory.Object;
        }
    }
}

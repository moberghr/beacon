using System.Text.Json;
using Beacon.Core;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.DataQuality.UpdateDataContract;
using Beacon.Core.Handlers.Recipients;
using Beacon.Core.Models;
using Beacon.Core.Models.DataQuality;
using Beacon.Core.Models.Recipients;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// The order in which a save of a governed resource reaches the database, on PostgreSQL and SQL Server, captured without
/// one: under the workflow the transaction starts, the resource's row is taken (a no-op update, as a decision takes it),
/// then the resource is read under that lock, and only then are its pending requests read and superseded — a save that
/// read first could decide on a definition a concurrent decision is changing. With the workflow off no row is taken. Each
/// script stops at the first statement it does not answer; nothing is committed.
/// </summary>
[TestFixture]
public class ChangeLockOrderTests
{
    private const string Ada = "ext-ada";
    private const int RecipientId = 5;
    private const int ContractId = 6;
    private const string Approved = "https://hooks.example.com/services/approved-path";
    private const string Moved = "https://hooks.example.com/services/moved-path";

    private static readonly RecipientSecretProtector Protector = new(new EncryptionService(TestEncryptionKey.Value));

    [TestCase(false)]
    [TestCase(true)]
    public async Task UpdateRecipient_UnderTheWorkflow_TakesTheRowInTheTransaction_BeforeReadingTheRecipientAndItsRequests(bool sqlServer)
    {
        var capture = Capture(sqlServer)
            .ThenRowsAffected(1)
            .ThenRow(StoredRecipient())
            .ThenScalar(false)
            .ThenRow(77, Ada, "older-hash")
            .ThenRowsAffected(1);

        var act = () => RecipientHandler(capture, TestChangeApprovals.On()).Handle(
            new UpdateRecipientCommand(RecipientId, "ops hook", null, Moved, (int)NotificationType.Webhook, null, null),
            CancellationToken.None);

        await StopsAtTheCapture(act);
        ShouldTakeTheRowFirst(capture, sqlServer, Table.Recipients, nameCheck: true);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RecipientServiceUpdate_UnderTheWorkflow_TakesTheRowInTheTransaction_BeforeReadingTheRecipientAndItsRequests(bool sqlServer)
    {
        var capture = Capture(sqlServer)
            .ThenRowsAffected(1)
            .ThenRow(StoredRecipient())
            .ThenRow(77, Ada, "older-hash")
            .ThenRowsAffected(1);

        var act = () => RecipientService(capture, TestChangeApprovals.On()).UpdateRecipient(RecipientData(), CancellationToken.None);

        await StopsAtTheCapture(act);
        ShouldTakeTheRowFirst(capture, sqlServer, Table.Recipients, nameCheck: false);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task UpdateDataContract_UnderTheWorkflow_TakesTheRowInTheTransaction_BeforeReadingTheContractAndItsRequests(bool sqlServer)
    {
        var capture = ContractChecks(Capture(sqlServer))
            .ThenRowsAffected(1)
            .ThenRow(StoredContract())
            .ThenRow(StoredRule())
            .ThenRowsAffected(1)
            .ThenRow(77, Ada, "older-hash")
            .ThenRowsAffected(1);

        var act = () => ContractHandler(capture, TestChangeApprovals.On()).Handle(ContractUpdate(), CancellationToken.None);

        await StopsAtTheCapture(act);
        var timeline = capture.Timeline.ToList();
        var begun = timeline.IndexOf(SqlCapture.TransactionBegun);
        begun.Should().Be(3, "the owner, the Custom SQL check and the catalog are read before the transaction");
        ShouldBeTheLock(timeline[begun + 1], sqlServer, Table.DataContracts);
        timeline[begun + 2].Should().Contain(sqlServer ? "FROM [beacon].[DataContracts]" : "FROM beacon.data_contracts").And.NotStartWith("UPDATE");
        timeline[begun + 3].Should().StartWith("SELECT").And.Contain(sqlServer ? "FROM [beacon].[DataContractRules]" : "FROM beacon.data_contract_rules");
        timeline[begun + 4].Should().StartWith("UPDATE").And.Contain(sqlServer ? "[ArchivedTime] = @" : "archived_time = @");
        ShouldReadThenSupersedeRequests(timeline[begun + 5], timeline[begun + 6], sqlServer);
        capture.Commits.Should().Be(0);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task UpdateRecipient_WithTheWorkflowOff_TakesNoRowAndStartsNoTransaction(bool sqlServer)
    {
        var capture = Capture(sqlServer).ThenNoRows();

        var act = () => RecipientHandler(capture, new BeaconConfiguration()).Handle(
            new UpdateRecipientCommand(RecipientId, "ops hook", null, Moved, (int)NotificationType.Webhook, null, null),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"Recipient {RecipientId} not found.");
        capture.Timeline.Should().ContainSingle().Which.Should().StartWith("SELECT");
        capture.TransactionIsolationLevels.Should().BeEmpty();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RecipientServiceUpdate_WithTheWorkflowOff_TakesNoRowAndStartsNoTransaction(bool sqlServer)
    {
        var capture = Capture(sqlServer).ThenNoRows();

        var act = () => RecipientService(capture, new BeaconConfiguration()).UpdateRecipient(RecipientData(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        capture.Timeline.Should().ContainSingle().Which.Should().StartWith("SELECT");
        capture.TransactionIsolationLevels.Should().BeEmpty();
    }

    // The contract's row is taken first in the save's transaction, also with the workflow off: saves of one contract take turns.
    [TestCase(false)]
    [TestCase(true)]
    public async Task UpdateDataContract_WithTheWorkflowOff_TakesTheContractRowFirst(bool sqlServer)
    {
        var capture = ContractChecks(Capture(sqlServer)).ThenRowsAffected(1).ThenNoRows();

        var act = () => ContractHandler(capture, new BeaconConfiguration()).Handle(ContractUpdate(), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconException>().WithMessage($"Data contract {ContractId} not found");
        var timeline = capture.Timeline;
        timeline.Should().HaveCount(6);
        timeline[3].Should().Be(SqlCapture.TransactionBegun);
        timeline[4].Should().StartWith("UPDATE").And.Contain(sqlServer ? "[DataContracts]" : "data_contracts");
        timeline[5].Should().StartWith("SELECT");
    }

    private enum Table
    {
        Recipients,
        DataContracts
    }

    // The statements a contract save sends before its transaction: its owner, whether it carries a Custom SQL rule, and the
    // table's metadata (one column).
    private static SqlCapture ContractChecks(SqlCapture capture) =>
        capture
            .ThenRow(DBNull.Value)
            .ThenScalar(true)
            .ThenRow("public", "orders", 1, "id", 10);

    // The recipient as stored: a governed webhook on its approved destination, with no recorded creator or modifier.
    private static object[] StoredRecipient() =>
    [
        RecipientId,
        RecipientDestinationDefinition.Hash(TestChangeApprovals.Hasher, NotificationType.Webhook, Approved, null),
        DBNull.Value,
        DBNull.Value,
        DBNull.Value,
        DateTime.UtcNow,
        DBNull.Value,
        Protector.Protect(Approved),
        DBNull.Value,
        DBNull.Value,
        DBNull.Value,
        "ops hook",
        (int)NotificationType.Webhook
    ];

    // The contract as stored (no recorded creator or modifier) with no recipients: the contract's columns, then the (empty)
    // recipient join's.
    private static object[] StoredContract() =>
    [
        ContractId, false, DBNull.Value, DBNull.Value, DateTime.UtcNow, "0 * * * *", ChangeApprovalStore.Warehouse, DBNull.Value, 80, false, DBNull.Value, DBNull.Value, "orders contract", DBNull.Value, DBNull.Value, "public", "orders",
        DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value
    ];

    // The contract's one live rule: a Custom SQL rule waiting for approval.
    private static object[] StoredRule() =>
    [
        11, ChangeApprovalHashes.NothingApproved, DBNull.Value, DBNull.Value, JsonSerializer.Serialize(new { sql = "SELECT 1 AS passed" }), DateTime.UtcNow, ContractId, DBNull.Value, true, "custom", (int)DataContractRuleType.CustomSql, (int)DataContractSeverity.Medium, 1.0
    ];

    // The script ends after the request is superseded: the next statement is captured, not run, and nothing commits.
    private static async Task StopsAtTheCapture(Func<Task> act)
    {
        var thrown = await act.Should().ThrowAsync<Exception>();
        (thrown.Which is SqlCapturedException || thrown.Which.InnerException is SqlCapturedException).Should().BeTrue(thrown.Which.ToString());
    }

    private static void ShouldTakeTheRowFirst(SqlCapture capture, bool sqlServer, Table table, bool nameCheck)
    {
        var timeline = capture.Timeline;
        timeline[0].Should().Be(SqlCapture.TransactionBegun);
        ShouldBeTheLock(timeline[1], sqlServer, table);
        capture.CommandParameters[0].Values.Should().Contain(RecipientId);
        timeline[2].Should().StartWith("SELECT").And.Contain(sqlServer ? "FROM [beacon].[Recipients]" : "FROM beacon.recipients");
        var requests = nameCheck ? 4 : 3;
        ShouldReadThenSupersedeRequests(timeline[requests], timeline[requests + 1], sqlServer);
        timeline.Should().NotContain(SqlCapture.TransactionCommitted);
        capture.Commits.Should().Be(0);
    }

    private static void ShouldBeTheLock(string sql, bool sqlServer, Table table)
    {
        var lockSql = (table, sqlServer) switch
        {
            (Table.Recipients, false) => "UPDATE beacon.recipients AS r\nSET approved_destination_hash = r.approved_destination_hash\n",
            (Table.Recipients, true) => "UPDATE [r]\nSET [r].[ApprovedDestinationHash] = [r].[ApprovedDestinationHash]\n",
            (Table.DataContracts, false) => "UPDATE beacon.data_contracts AS d\nSET is_enabled = d.is_enabled\n",
            _ => "UPDATE [d]\nSET [d].[IsEnabled] = [d].[IsEnabled]\n"
        };

        sql.Should().StartWith(lockSql);
    }

    private static void ShouldReadThenSupersedeRequests(string read, string supersede, bool sqlServer)
    {
        read.Should().StartWith("SELECT").And.Contain(sqlServer ? "FROM [beacon].[ChangeApprovalRequests]" : "FROM beacon.change_approval_requests");
        supersede.Should().StartWith(sqlServer ? "UPDATE [c]" : "UPDATE beacon.change_approval_requests").And.EndWith(sqlServer ? "[c].[Status] = 1" : "c.status = 1");
    }

    private static RecipientData RecipientData() =>
        new()
        {
            RecipientId = RecipientId,
            Name = "ops hook",
            Destination = Moved,
            NotificationType = NotificationType.Webhook
        };

    private static UpdateDataContractCommand ContractUpdate() =>
        new(
            ContractId,
            ChangeApprovalStore.Warehouse,
            "public",
            "orders",
            "orders contract",
            null,
            "0 * * * *",
            false,
            false,
            80,
            [new DataContractRuleData { Name = "volume", RuleType = DataContractRuleType.Volume, Configuration = """{"minRows":1}""", IsEnabled = true }]);

    private static UpdateRecipientHandler RecipientHandler(SqlCapture capture, BeaconConfiguration configuration) =>
        new(
            capture.Factory(),
            new RecipientSecretEditor(NotificationTestKit.Policy(), Protector),
            TestProjectAccess.Evaluator(TestProjectAccess.Unrestricted),
            TestProjectAccess.Actors(),
            TestPermissions.Admin,
            TestChangeApprovals.Recipients(Protector, configuration, TestChangeApprovals.Caller(Ada)),
            new RecordingSecurityAudit(), TimeProvider.System,
            NullLogger<UpdateRecipientHandler>.Instance);

    private static RecipientService RecipientService(SqlCapture capture, BeaconConfiguration configuration) =>
        new(
            capture.Factory(),
            new RecipientSecretEditor(NotificationTestKit.Policy(), Protector),
            TestChangeApprovals.Recipients(Protector, configuration, TestChangeApprovals.Caller(Ada)), TestProjectAccess.Actors(), new RecordingSecurityAudit(), TimeProvider.System);

    private static UpdateDataContractHandler ContractHandler(SqlCapture capture, BeaconConfiguration configuration) =>
        new(
            capture.Factory(),
            Mock.Of<IBeaconScheduler>(),
            NotificationTestKit.UserContext(NotificationTestKit.Admin()),
            TestSqlGate.Create(),
            TestConfiguredDatabases.None,
            TestChangeApprovals.Caller(Ada, beaconUserId: 7, isAdmin: true),
            TestChangeApprovals.CustomSql(configuration, TestChangeApprovals.Caller(Ada, beaconUserId: 7, isAdmin: true)),
            new RecordingSecurityAudit(), TimeProvider.System,
            NullLogger<UpdateDataContractHandler>.Instance);

    private static SqlCapture Capture(bool sqlServer) => sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture();
}

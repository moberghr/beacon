using Beacon.Core;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.DataQuality.UpdateDataContract;
using Beacon.Core.Models;
using Beacon.Core.Models.DataQuality;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.ChangeGovernance;
using Beacon.Tests.Unit.Notifications;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.DataQuality;

/// <summary>
/// A data contract's rule history: a save that replaces the contract's rules retires the ones it replaces instead of
/// deleting them, and keeps the results of past evaluations; a rule another save retired meanwhile refuses the save; reads
/// and evaluations leave retired rules out, while the evaluation history keeps their results with the rule's name.
/// </summary>
[TestFixture]
public class ContractRuleHistoryTests
{
    private const int ContractId = 42;

    [Test]
    public async Task Update_RetiresTheRulesItReplaces_AndKeepsTheResultsOfPastEvaluations()
    {
        var store = new ChangeApprovalStore();
        var old = ChangeApprovalStore.Volume("old volume");
        var contract = store.AddContract(old);
        var result = new DataQualityRuleResult { Id = 900, DataQualityEvaluationId = 800, DataContractRuleId = old.Id, Passed = false, Message = "too few rows" };
        store.Results.Add(result);

        await UpdateHandler(store).Handle(
            new UpdateDataContractCommand(contract.Id, ChangeApprovalStore.Warehouse, "public", "orders", contract.Name, null, contract.CronExpression, false, false, 80, [Volume("new volume")]),
            CancellationToken.None);
        store.Commit();

        old.ArchivedTime.Should().NotBeNull("the replaced rule is retired, not deleted");
        store.Results.Should().ContainSingle().Which.Should().BeSameAs(result);
        result.DataContractRuleId.Should().Be(old.Id, "the result keeps pointing at the rule that produced it");
        var live = contract.Rules.Should().ContainSingle().Subject;
        live.Should().BeEquivalentTo(new { Name = "new volume", ArchivedTime = (DateTime?)null }, x => x.ExcludingMissingMembers());
        live.Id.Should().NotBe(old.Id);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Update_WhenAnotherSaveRetiredTheRulesMeanwhile_IsRefused_AndCommitsNothing(bool sqlServer)
    {
        var capture = (sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture())
            .ThenRow(DBNull.Value)
            .ThenScalar(false)
            .ThenRow("public", "orders", 1, "id", 10)
            .ThenRow(StoredContract())
            .ThenRow(StoredVolumeRule())
            .ThenRowsAffected(0);

        var act = () => CapturedHandler(capture).Handle(ContractUpdate(), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(UpdateDataContractHandler.RulesReplacedMessage);
        var retire = capture.Timeline.Should().ContainSingle(x => x.StartsWith("UPDATE", StringComparison.Ordinal)).Subject;
        retire.Should().Contain(sqlServer ? "[ArchivedTime] = @" : "archived_time = @")
            .And.Contain(sqlServer ? "[ArchivedTime] IS NULL" : "archived_time IS NULL", "a rule another save retired is not retired again");
        capture.Timeline.Should().NotContain(x => x.StartsWith("DELETE", StringComparison.Ordinal), "nothing is deleted");
        capture.Commits.Should().Be(0);
    }

    [TestCase(false, "archived_time IS NULL")]
    [TestCase(true, "[ArchivedTime] IS NULL")]
    public async Task Evaluate_ReadsOnlyTheContractsLiveRules(bool sqlServer, string liveFilter)
    {
        var capture = (sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture()).ThenNoRows();

        var act = () => EvaluationService(capture).EvaluateContractAsync(ContractId, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconException>().WithMessage($"Data contract {ContractId} not found");
        capture.Timeline.Should().ContainSingle()
            .Which.Should().Contain(sqlServer ? "[DataContractRules]" : "data_contract_rules").And.Contain(liveFilter);
    }

    [TestCase(false, "archived_time")]
    [TestCase(true, "[ArchivedTime]")]
    public async Task History_KeepsTheResultsOfRetiredRules_WithTheRulesName(bool sqlServer, string archived)
    {
        var capture = (sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture()).ThenNoRows();

        var history = await EvaluationService(capture).GetEvaluationHistoryAsync(ContractId, cancellationToken: CancellationToken.None);

        history.Should().BeEmpty();
        capture.Timeline.Should().ContainSingle()
            .Which.Should().Contain(sqlServer ? "[DataContractRules]" : "data_contract_rules").And.NotContain(archived, "retired rules' results stay in the history");
    }

    [Test]
    public void ContractRules_LeaveRetiredRulesOut_Translates()
    {
        using var npgsql = NpgsqlTestContext.Create();
        using var sqlServer = SqlServerTestContext.Create();

        Rules(npgsql).ToQueryString().Should().Contain("archived_time IS NULL");
        Rules(sqlServer).ToQueryString().Should().Contain("[ArchivedTime] IS NULL");

        static IQueryable<List<string>> Rules(Beacon.Core.Data.BeaconContext context) =>
            context.DataContracts
                .Where(x => x.Id == ContractId)
                .Select(x => x.Rules
                    .Select(y => y.Name)
                    .ToList());
    }

    private static DataContractRuleData Volume(string name) =>
        new()
        {
            Name = name,
            RuleType = DataContractRuleType.Volume,
            Configuration = """{"minRows":1}""",
            IsEnabled = true
        };

    private static UpdateDataContractCommand ContractUpdate() =>
        new(ContractId, ChangeApprovalStore.Warehouse, "public", "orders", "orders contract", null, "0 * * * *", false, false, 80, [Volume("volume")]);

    // The contract as stored (no recorded creator or modifier) with no recipients: the contract's columns, then the (empty)
    // recipient join's.
    private static object[] StoredContract() =>
    [
        ContractId, false, DBNull.Value, DBNull.Value, DateTime.UtcNow, "0 * * * *", ChangeApprovalStore.Warehouse, DBNull.Value, 80, false, DBNull.Value, DBNull.Value, "orders contract", DBNull.Value, DBNull.Value, "public", "orders",
        DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value
    ];

    // One live Volume rule of the contract.
    private static object[] StoredVolumeRule() =>
    [
        11, DBNull.Value, DBNull.Value, DBNull.Value, """{"minRows":1}""", DateTime.UtcNow, ContractId, DBNull.Value, true, "volume", (int)DataContractRuleType.Volume, (int)DataContractSeverity.Medium, 1.0
    ];

    private static UpdateDataContractHandler UpdateHandler(ChangeApprovalStore store) =>
        Handler(store.Factory());

    private static UpdateDataContractHandler CapturedHandler(SqlCapture capture) =>
        Handler(capture.Factory());

    private static UpdateDataContractHandler Handler(IDbContextFactory<Beacon.Core.Data.BeaconContext> factory)
    {
        var configuration = new BeaconConfiguration();
        var admin = TestChangeApprovals.Caller("ext-ada", beaconUserId: 7, isAdmin: true);

        return new(
            factory,
            Mock.Of<IBeaconScheduler>(),
            NotificationTestKit.UserContext(NotificationTestKit.Admin()),
            TestSqlGate.Create(),
            TestConfiguredDatabases.None,
            admin,
            TestChangeApprovals.CustomSql(configuration, admin),
            new RecordingSecurityAudit(), TimeProvider.System,
            NullLogger<UpdateDataContractHandler>.Instance);
    }

    private static DataQualityEvaluationService EvaluationService(SqlCapture capture) =>
        new(
            capture.Factory(),
            new DataQualitySqlGenerator(),
            new DataSourceProviderFactory([]),
            TestSqlGate.Create(),
            TestConfiguredDatabases.None,
            TestProjectAccess.ExecutionAllowed(),
            TestRowSecurity.Resolver,
            TestQueryServices.DefaultSettings(),
            new QueryGuardrailService(),
            new BeaconConfiguration(),
            NullLogger<DataQualityEvaluationService>.Instance);
}

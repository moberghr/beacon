using System.Text.Json;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.DataQuality.CreateDataContract;
using Beacon.Core.Handlers.DataQuality.GetDataContractDetail;
using Beacon.Core.Handlers.DataQuality.UpdateDataContract;
using Beacon.Core.Models;
using Beacon.Core.Models.Approvals;
using Beacon.Core.Models.DataQuality;
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
/// Data contracts' Custom SQL rules under the change approval workflow: a rule that is new, or whose SQL or data source
/// changed, waits for a second person's approval and is not evaluated meanwhile; an unchanged rule keeps its approval (or,
/// stored before, stays ungoverned) although a save rewrites the contract's rules; the rules waiting form one request per
/// contract, superseded by the requester's own next change of them and refused while someone else's waits; approving it
/// approves each of them. Without the workflow nothing changes.
/// </summary>
[TestFixture]
public class CustomSqlRuleApprovalTests
{
    private const string Ada = "ext-ada";
    private const string CountSql = "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END AS passed FROM orders";
    private const string NullSql = "SELECT CASE WHEN COUNT(*) = 0 THEN 1 ELSE 0 END AS passed FROM orders WHERE id IS NULL";

    [Test]
    public async Task Create_UnderTheWorkflow_HoldsItsCustomSqlRules_AndSubmitsThem_InOneCommit()
    {
        var store = new ChangeApprovalStore();

        var created = await CreateHandler(store, TestChangeApprovals.On()).Handle(CreateCommand(Rule(CountSql), Volume()), CancellationToken.None);
        store.Commit();

        var contract = store.Contracts.Should().ContainSingle().Subject;
        contract.Id.Should().Be(created.DataContractId);
        created.Should().BeEquivalentTo(new { AwaitingApproval = true, Message = CreateDataContractResult.SubmittedForApprovalMessage }, x => x.ExcludingMissingMembers());
        contract.Rules.Single(x => x.RuleType == DataContractRuleType.CustomSql).ApprovedSqlHash.Should().Be(ChangeApprovalHashes.NothingApproved);
        contract.Rules.Single(x => x.RuleType == DataContractRuleType.Volume).ApprovedSqlHash.Should().BeNull();
        var request = store.Requests.Should().ContainSingle().Subject;
        request.Should().BeEquivalentTo(
            new
            {
                Kind = ChangeApprovalKind.CustomSqlRules,
                ResourceId = contract.Id,
                RequestedByUserId = Ada,
                DefinitionHash = CustomSqlRuleDefinition.SetHash([CustomSqlRuleDefinition.Hash(ChangeApprovalStore.Warehouse, Config(CountSql))]),
                ChangeSummary = "Contract created with 1 Custom SQL rule",
                PreviousJson = (string?)null
            },
            x => x.ExcludingMissingMembers());
        Read(request.ProposedJson).Should().BeEquivalentTo(new
        {
            DataSourceId = ChangeApprovalStore.Warehouse,
            Rules = new[] { new { Name = "custom", Sql = CountSql, AwaitsApproval = true } }
        });
        store.Offline.Commits.Should().Be(1);
    }

    [TestCase(false, true, true)]
    [TestCase(true, false, true)]
    [TestCase(true, true, false)]
    public async Task Create_WithTheWorkflowOrItsSwitchOff_OrWithoutCustomSql_SubmitsNothing(bool enabled, bool covers, bool customSql)
    {
        var store = new ChangeApprovalStore();
        var configuration = new BeaconConfiguration { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = enabled, CoversCustomSqlRules = covers } };

        var created = await CreateHandler(store, configuration).Handle(customSql ? CreateCommand(Rule(CountSql)) : CreateCommand(Volume()), CancellationToken.None);
        store.Commit();

        created.Should().BeEquivalentTo(new { AwaitingApproval = false, Message = (string?)null }, x => x.ExcludingMissingMembers());
        store.Contracts.Single().Rules.Should().OnlyContain(x => x.ApprovedSqlHash == null);
        store.Requests.Should().BeEmpty();
        store.Offline.TransactionIsolationLevels.Should().BeEmpty();
    }

    // A save rewrites every rule; an approved Custom SQL rule whose SQL stays keeps its approval, and nothing is submitted.
    [Test]
    public async Task Update_ThatKeepsTheApprovedSql_KeepsItsApproval_AndSubmitsNothing()
    {
        var store = new ChangeApprovalStore();
        var approved = ChangeApprovalStore.CustomSql(CountSql, approvedSqlHash: Hash(CountSql));
        var contract = store.AddContract(approved, ChangeApprovalStore.Volume());

        await UpdateHandler(store, TestChangeApprovals.On()).Handle(UpdateCommand(contract, Rule(CountSql), Volume()), CancellationToken.None);
        store.Commit();

        contract.Rules.Single(x => x.RuleType == DataContractRuleType.CustomSql).ApprovedSqlHash.Should().Be(Hash(CountSql));
        store.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task Update_ThatChangesOneRulesSql_HoldsOnlyThatRule_AndShowsTheRulesBeforeAndAfter()
    {
        var store = new ChangeApprovalStore();
        var contract = store.AddContract(
            ChangeApprovalStore.CustomSql(CountSql, "counts", Hash(CountSql)),
            ChangeApprovalStore.CustomSql(NullSql, "nulls", Hash(NullSql)));
        const string changed = NullSql + " AND 1 = 1";

        await UpdateHandler(store, TestChangeApprovals.On()).Handle(UpdateCommand(contract, Rule(CountSql, "counts"), Rule(changed, "nulls")), CancellationToken.None);
        store.Commit();

        contract.Rules.Select(x => (x.Name, x.ApprovedSqlHash)).Should().Equal(("counts", Hash(CountSql)), ("nulls", Hash(NullSql)));
        contract.Rules.Select(x => CustomSqlRuleDefinition.Awaits(x, ChangeApprovalStore.Warehouse)).Should().Equal(false, true);
        var request = store.Requests.Should().ContainSingle().Subject;
        request.DefinitionHash.Should().Be(CustomSqlRuleDefinition.SetHash([Hash(changed)]));
        request.ChangeSummary.Should().Be("1 Custom SQL rule added or changed");
        Read(request.PreviousJson!).Rules.Select(x => (x.Name, x.Sql, x.AwaitsApproval)).Should().Equal(("counts", CountSql, false), ("nulls", NullSql, false));
        Read(request.ProposedJson).Rules.Select(x => (x.Name, x.Sql, x.AwaitsApproval)).Should().Equal(("counts", CountSql, false), ("nulls", changed, true));
    }

    // Grandfathered: a rule stored before the workflow governed it stays ungoverned while its SQL stays; a rule added next
    // to it waits.
    [Test]
    public async Task Update_OfAContractStoredBefore_LeavesItsUnchangedRuleUngoverned_AndHoldsTheNewOne()
    {
        var store = new ChangeApprovalStore();
        var contract = store.AddContract(ChangeApprovalStore.CustomSql(CountSql, "counts"));

        await UpdateHandler(store, TestChangeApprovals.On()).Handle(UpdateCommand(contract, Rule(CountSql, "counts"), Rule(NullSql, "nulls")), CancellationToken.None);
        store.Commit();

        contract.Rules.Select(x => (x.Name, x.ApprovedSqlHash)).Should().Equal(("counts", (string?)null), ("nulls", ChangeApprovalHashes.NothingApproved));
        store.Requests.Should().ContainSingle().Which.DefinitionHash.Should().Be(CustomSqlRuleDefinition.SetHash([Hash(NullSql)]));
    }

    // Grandfathered and changed: the rule ran its SQL before the workflow, so that SQL counts as approved for it — the new
    // SQL waits, and putting the old SQL back needs no approval (the requester's own request is withdrawn).
    [Test]
    public async Task Update_OfAGrandfatheredRulesSql_HoldsItWithTheOldSqlApproved_AndPuttingItBackNeedsNoApproval()
    {
        var store = new ChangeApprovalStore();
        var contract = store.AddContract(ChangeApprovalStore.CustomSql(CountSql, "counts"));

        await UpdateHandler(store, TestChangeApprovals.On()).Handle(UpdateCommand(contract, Rule(NullSql, "counts")), CancellationToken.None);
        store.Commit();

        var changed = contract.Rules.Single();
        changed.ApprovedSqlHash.Should().Be(Hash(CountSql), "the SQL it ran before the workflow is carried as approved");
        CustomSqlRuleDefinition.Awaits(changed, ChangeApprovalStore.Warehouse).Should().BeTrue();
        var pending = store.Requests.Should().ContainSingle().Subject;
        pending.DefinitionHash.Should().Be(CustomSqlRuleDefinition.SetHash([Hash(NullSql)]));

        await UpdateHandler(store, TestChangeApprovals.On()).Handle(UpdateCommand(contract, Rule(CountSql, "counts")), CancellationToken.None);
        store.Commit();

        var restored = contract.Rules.Single();
        restored.ApprovedSqlHash.Should().Be(Hash(CountSql));
        CustomSqlRuleDefinition.Awaits(restored, ChangeApprovalStore.Warehouse).Should().BeFalse();
        pending.Status.Should().Be(ApprovalStatus.Superseded);
        store.Requests.Should().ContainSingle("putting the old SQL back submits nothing");
    }

    // Renamed and changed at once, the rule matches nothing it replaces: it is new, with nothing approved.
    [Test]
    public async Task Update_ThatRenamesAndChangesAGrandfatheredRule_HoldsItWithNothingApproved()
    {
        var store = new ChangeApprovalStore();
        var contract = store.AddContract(ChangeApprovalStore.CustomSql(CountSql, "counts"));

        await UpdateHandler(store, TestChangeApprovals.On()).Handle(UpdateCommand(contract, Rule(NullSql, "nulls")), CancellationToken.None);
        store.Commit();

        contract.Rules.Single().Should().BeEquivalentTo(new { Name = "nulls", ApprovedSqlHash = ChangeApprovalHashes.NothingApproved }, x => x.ExcludingMissingMembers());
        store.Requests.Should().ContainSingle().Which.DefinitionHash.Should().Be(CustomSqlRuleDefinition.SetHash([Hash(NullSql)]));
    }

    // The same SQL on another data source reads other data: every Custom SQL rule waits again.
    [Test]
    public async Task Update_ThatMovesTheContractToAnotherDataSource_HoldsEveryCustomSqlRule()
    {
        var store = new ChangeApprovalStore();
        var contract = store.AddContract(ChangeApprovalStore.CustomSql(CountSql, "counts", Hash(CountSql)), ChangeApprovalStore.CustomSql(NullSql, "nulls"));

        await UpdateHandler(store, TestChangeApprovals.On()).Handle(UpdateCommand(contract, ChangeApprovalStore.Ledger, Rule(CountSql, "counts"), Rule(NullSql, "nulls")), CancellationToken.None);
        store.Commit();

        contract.Rules.Should().OnlyContain(x => CustomSqlRuleDefinition.Awaits(x, ChangeApprovalStore.Ledger));
        store.Requests.Single().DefinitionHash.Should().Be(CustomSqlRuleDefinition.SetHash(
            [CustomSqlRuleDefinition.Hash(ChangeApprovalStore.Ledger, Config(CountSql)), CustomSqlRuleDefinition.Hash(ChangeApprovalStore.Ledger, Config(NullSql))]));
    }

    // A changed rule keeps the SQL approved for it: putting that SQL back needs no approval, and withdraws the requester's
    // own pending request for the change.
    [Test]
    public async Task Update_ThatPutsTheApprovedSqlBack_NeedsNoApproval_AndSupersedesTheOwnRequest()
    {
        var store = new ChangeApprovalStore();
        var contract = store.AddContract(ChangeApprovalStore.CustomSql(CountSql, "counts", Hash(CountSql)));
        await UpdateHandler(store, TestChangeApprovals.On()).Handle(UpdateCommand(contract, Rule(NullSql, "counts")), CancellationToken.None);
        store.Commit();
        var pending = store.Requests.Single();

        await UpdateHandler(store, TestChangeApprovals.On()).Handle(UpdateCommand(contract, Rule(CountSql, "counts")), CancellationToken.None);
        store.Commit();

        contract.Rules.Single().ApprovedSqlHash.Should().Be(Hash(CountSql));
        CustomSqlRuleDefinition.Awaits(contract.Rules.Single(), ChangeApprovalStore.Warehouse).Should().BeFalse();
        pending.Status.Should().Be(ApprovalStatus.Superseded);
        store.Requests.Should().ContainSingle();
    }

    // Removing the rule that waited leaves nothing to approve: the requester's own pending request is withdrawn.
    [Test]
    public async Task Update_ThatRemovesTheWaitingRule_SupersedesTheRequestersOwnRequest()
    {
        var store = new ChangeApprovalStore();
        var contract = store.AddContract(ChangeApprovalStore.CustomSql(CountSql, "counts", Hash(CountSql)), ChangeApprovalStore.CustomSql(NullSql, "nulls", ChangeApprovalHashes.NothingApproved));
        var pending = store.AddRequest(ChangeApprovalKind.CustomSqlRules, contract.Id, CustomSqlRuleDefinition.SetHash([Hash(NullSql)]), Ada);

        await UpdateHandler(store, TestChangeApprovals.On()).Handle(UpdateCommand(contract, Rule(CountSql, "counts")), CancellationToken.None);
        store.Commit();

        pending.Status.Should().Be(ApprovalStatus.Superseded);
        store.Requests.Should().ContainSingle();
    }

    [Test]
    public async Task Update_ThatChangesTheWaitingRules_WhileAnotherPersonsRequestWaits_IsRefused()
    {
        var store = new ChangeApprovalStore();
        var contract = store.AddContract(ChangeApprovalStore.CustomSql(NullSql, "nulls", ChangeApprovalHashes.NothingApproved));
        var pending = store.AddRequest(ChangeApprovalKind.CustomSqlRules, contract.Id, CustomSqlRuleDefinition.SetHash([Hash(NullSql)]), "ext-ben");

        var act = () => UpdateHandler(store, TestChangeApprovals.On()).Handle(UpdateCommand(contract, Rule(CountSql, "nulls")), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(ChangeApprovalSubmitter.OtherPendingMessage(ChangeApprovalKind.CustomSqlRules));
        pending.Status.Should().Be(ApprovalStatus.Pending);
        store.Offline.Commits.Should().Be(0);
    }

    // A save that leaves the waiting rules as they are (a new schedule) neither touches someone else's request nor adds one.
    [Test]
    public async Task Update_ThatLeavesTheWaitingRulesAsTheyAre_KeepsAnotherPersonsRequest()
    {
        var store = new ChangeApprovalStore();
        var contract = store.AddContract(ChangeApprovalStore.CustomSql(NullSql, "nulls", ChangeApprovalHashes.NothingApproved));
        var pending = store.AddRequest(ChangeApprovalKind.CustomSqlRules, contract.Id, CustomSqlRuleDefinition.SetHash([Hash(NullSql)]), "ext-ben");

        await UpdateHandler(store, TestChangeApprovals.On()).Handle(UpdateCommand(contract, Rule(NullSql, "nulls")) with { CronExpression = "0 6 * * *" }, CancellationToken.None);
        store.Commit();

        pending.Status.Should().Be(ApprovalStatus.Pending);
        store.Requests.Should().ContainSingle();
        contract.CronExpression.Should().Be("0 6 * * *");
    }

    [Test]
    public async Task Update_WithTheWorkflowOff_LeavesANewOrChangedRuleUngoverned()
    {
        var store = new ChangeApprovalStore();
        var contract = store.AddContract(ChangeApprovalStore.CustomSql(CountSql, "counts", Hash(CountSql)));

        await UpdateHandler(store, new BeaconConfiguration()).Handle(UpdateCommand(contract, Rule(CountSql, "counts"), Rule(NullSql, "nulls")), CancellationToken.None);
        store.Commit();

        contract.Rules.Select(x => (x.Name, x.ApprovedSqlHash)).Should().Equal(("counts", Hash(CountSql)), ("nulls", (string?)null));
        store.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task Approve_ApprovesEachWaitingRule_AndTheContractNoLongerShowsThemWaiting()
    {
        var store = new ChangeApprovalStore();
        var contract = store.AddContract(
            ChangeApprovalStore.CustomSql(CountSql, "counts", Hash(CountSql)),
            ChangeApprovalStore.CustomSql(NullSql, "nulls", ChangeApprovalHashes.NothingApproved));
        var request = store.AddRequest(ChangeApprovalKind.CustomSqlRules, contract.Id, CustomSqlRuleDefinition.SetHash([Hash(NullSql)]), "ext-ben");
        var before = await DetailHandler(store).Handle(new GetDataContractDetailQuery(contract.Id), CancellationToken.None);

        await Approvals(store).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);
        var after = await DetailHandler(store).Handle(new GetDataContractDetailQuery(contract.Id), CancellationToken.None);

        contract.Rules.Select(x => x.ApprovedSqlHash).Should().Equal(Hash(CountSql), Hash(NullSql));
        before.Rules.Select(x => (x.Name, x.AwaitingApproval)).Should().Equal(("counts", false), ("nulls", true));
        before.PendingApprovalRequestId.Should().Be(request.Id);
        after.Rules.Should().OnlyContain(x => !x.AwaitingApproval);
        after.PendingApprovalRequestId.Should().BeNull();
    }

    // Approval binds to the waiting rules as submitted: one changed since (outside the workflow) makes the request stale.
    [Test]
    public async Task Approve_WhenTheWaitingRulesChangedSinceTheRequest_Is409()
    {
        var store = new ChangeApprovalStore();
        var waiting = ChangeApprovalStore.CustomSql(NullSql, "nulls", ChangeApprovalHashes.NothingApproved);
        var contract = store.AddContract(waiting);
        var request = store.AddRequest(ChangeApprovalKind.CustomSqlRules, contract.Id, CustomSqlRuleDefinition.SetHash([Hash(NullSql)]), "ext-ben");
        waiting.Configuration = Config(CountSql);

        var act = () => Approvals(store).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(ChangeApprovalService.ChangedSinceSubmittedMessage);
        waiting.ApprovedSqlHash.Should().Be(ChangeApprovalHashes.NothingApproved);
        request.Status.Should().Be(ApprovalStatus.Pending);
    }

    [Test]
    public void TheRuleHash_CoversTheSqlCharacterForCharacter_AndTheDataSource_NotTheRestOfTheConfiguration()
    {
        var hash = Hash(CountSql);

        CustomSqlRuleDefinition.Hash(ChangeApprovalStore.Warehouse, JsonSerializer.Serialize(new { sql = CountSql, note = "x" })).Should().Be(hash);
        Hash(CountSql + " ").Should().NotBe(hash);
        CustomSqlRuleDefinition.Hash(ChangeApprovalStore.Ledger, Config(CountSql)).Should().NotBe(hash);
        CustomSqlRuleDefinition.SetHash(["b", "a"]).Should().Be(CustomSqlRuleDefinition.SetHash(["a", "b"]));
        CustomSqlRuleDefinition.SetHash(["a", "a"]).Should().NotBe(CustomSqlRuleDefinition.SetHash(["a"]));
    }

    private static string Config(string sql) => JsonSerializer.Serialize(new { sql });

    private static string Hash(string sql) => CustomSqlRuleDefinition.Hash(ChangeApprovalStore.Warehouse, Config(sql));

    private static CustomSqlRulesView Read(string json) =>
        JsonSerializer.Deserialize<CustomSqlRulesView>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static DataContractRuleData Rule(string sql, string name = "custom") =>
        new()
        {
            Name = name,
            RuleType = DataContractRuleType.CustomSql,
            Configuration = Config(sql),
            IsEnabled = true
        };

    private static DataContractRuleData Volume() =>
        new()
        {
            Name = "volume",
            RuleType = DataContractRuleType.Volume,
            Configuration = """{"minRows":1}""",
            IsEnabled = true
        };

    private static CreateDataContractCommand CreateCommand(params DataContractRuleData[] rules) =>
        new(ChangeApprovalStore.Warehouse, "public", "orders", "orders contract", null, "0 * * * *", false, false, 80, [.. rules]);

    private static UpdateDataContractCommand UpdateCommand(DataContract contract, params DataContractRuleData[] rules) =>
        UpdateCommand(contract, ChangeApprovalStore.Warehouse, rules);

    private static UpdateDataContractCommand UpdateCommand(DataContract contract, int dataSourceId, params DataContractRuleData[] rules) =>
        new(contract.Id, dataSourceId, "public", "orders", contract.Name, null, contract.CronExpression, false, false, 80, [.. rules]);

    private static IBeaconActorAccessor Admin() => TestChangeApprovals.Caller(Ada, beaconUserId: 7, isAdmin: true);

    private static CreateDataContractHandler CreateHandler(ChangeApprovalStore store, BeaconConfiguration configuration) =>
        new(
            store.Factory(),
            Mock.Of<IBeaconScheduler>(),
            NotificationTestKit.UserContext(NotificationTestKit.Admin()),
            TestSqlGate.Create(),
            TestConfiguredDatabases.None,
            Admin(),
            configuration,
            TestChangeApprovals.CustomSql(configuration, Admin()), new RecordingSecurityAudit());

    private static UpdateDataContractHandler UpdateHandler(ChangeApprovalStore store, BeaconConfiguration configuration) =>
        new(
            store.Factory(),
            Mock.Of<IBeaconScheduler>(),
            NotificationTestKit.UserContext(NotificationTestKit.Admin()),
            TestSqlGate.Create(),
            TestConfiguredDatabases.None,
            Admin(),
            TestChangeApprovals.CustomSql(configuration, Admin()),
            new RecordingSecurityAudit(), TimeProvider.System,
            NullLogger<UpdateDataContractHandler>.Instance);

    private static GetDataContractDetailHandler DetailHandler(ChangeApprovalStore store) => new(store.Factory(), TestChangeApprovals.On());

    private static ChangeApprovalService Approvals(ChangeApprovalStore store) =>
        new(
            store.Factory(),
            TestChangeApprovals.Caller("ext-cid"),
            Mock.Of<IBeaconUserContext>(),
            store.Protector,
            TestChangeApprovals.Hasher,
            TestChangeApprovals.On(),
            new RecordingSecurityAudit(),
            TestPermissions.Admin,
            TestChangeApprovals.NoDefinitions,
            TestChangeApprovals.NoProbe,
            TestChangeApprovals.Llm(),
            NullLogger<ChangeApprovalService>.Instance);
}

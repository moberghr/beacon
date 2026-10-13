using System.Text.Json;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Approvals;
using Beacon.Core.Handlers.DataQuality.CreateDataContract;
using Beacon.Core.Handlers.DataQuality.GetDataContractDetail;
using Beacon.Core.Handlers.DataQuality.UpdateDataContract;
using Beacon.Core.Handlers.Recipients;
using Beacon.Core.Models;
using Beacon.Core.Models.DataQuality;
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
/// What follows a change decision, and who reads change requests. A resource that waits while no request of it is
/// pending shows the last decision on it (a rejection and its comment) to whoever reads the resource. A save made while
/// the workflow does not review a resource, and that changes what its pending request proposes, supersedes that request.
/// A contract whose rules gained a Custom SQL rule while it was being saved is refused, not saved without a requester.
/// Creating a contract under the workflow writes its recipients' links only, never the recipients. An Approver reads
/// every change request; anyone else, those of the migration jobs and data contracts it reads, never a recipient's.
/// </summary>
[TestFixture]
public class ChangeApprovalOutcomeTests
{
    private const string Ada = "ext-ada";
    private const string CountSql = "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END AS passed FROM orders";
    private const string Destination = "https://hooks.example.com/services/T0KEN-PATH-9f8e";
    private const string Moved = "https://other.example.org/in/T0KEN-PATH-9f8e";

    // The contract and its request are saved after the tracker was cleared: the recipients it names are attached as they
    // are stored, so the save inserts the contract, its rules and its links, and writes no recipient.
    [Test]
    public async Task Create_UnderTheWorkflow_WithRecipients_WritesTheirLinksOnly_NeverTheRecipients()
    {
        var store = new ChangeApprovalStore();
        var first = store.AddRecipient(Destination);
        var second = store.AddRecipient(Moved, ChangeApprovalStore.HookHash(Moved));

        var created = await CreateHandler(store, trackContracts: true).Handle(CreateCommand([first.Id, second.Id], Rule(CountSql)), CancellationToken.None);

        created.AwaitingApproval.Should().BeTrue();
        store.Saved.OfType<Recipient>().Should().BeEmpty("the recipients are linked, not inserted or rewritten");
        var contract = store.Saved.OfType<DataContract>().Distinct().Should().ContainSingle().Subject;
        contract.Recipients.Should().BeEquivalentTo([first, second], x => x.WithoutStrictOrdering().Including(y => y.Id));
        store.Offline.Commits.Should().Be(1);
    }

    // Without the workflow nobody reviews the recipient: a save that changes where it delivers stops governing it, and
    // every pending request of it — whoever made it — is superseded and its requester told.
    [Test]
    public async Task Update_WithTheWorkflowOff_OfAGovernedRecipient_SupersedesItsPendingRequests()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Moved, ChangeApprovalStore.HookHash(Destination));
        var pending = store.AddRequest(ChangeApprovalKind.RecipientDestination, recipient.Id, ChangeApprovalStore.HookHash(Moved), "ext-ben");
        var notifier = new Mock<IApprovalNotifier>();

        await RecipientUpdateHandler(store, new BeaconConfiguration(), notifier.Object)
            .Handle(new UpdateRecipientCommand(recipient.Id, recipient.Name, null, "https://third.example.net/in", (int)NotificationType.Webhook, null, null), CancellationToken.None);

        recipient.ApprovedDestinationHash.Should().BeNull();
        pending.Status.Should().Be(ApprovalStatus.Superseded);
        notifier.Verify(x => x.ApprovalUpdatedAsync(pending.Id, "superseded", null, "ext-ben", It.IsAny<CancellationToken>()), Times.Once);
    }

    // Without the workflow, a save that changes which Custom SQL rules wait supersedes the request of those that waited;
    // one that leaves them as they are keeps it.
    [TestCase(true, ApprovalStatus.Superseded)]
    [TestCase(false, ApprovalStatus.Pending)]
    public async Task Update_WithTheWorkflowOff_SupersedesTheRequest_OnlyWhenTheWaitingRulesChange(bool changeTheWaitingRule, ApprovalStatus expected)
    {
        var store = new ChangeApprovalStore();
        var contract = store.AddContract(ChangeApprovalStore.CustomSql(CountSql, approvedSqlHash: ChangeApprovalHashes.NothingApproved), ChangeApprovalStore.Volume());
        var pending = store.AddRequest(ChangeApprovalKind.CustomSqlRules, contract.Id, CustomSqlRuleDefinition.SetHash([SqlHash(CountSql)]), "ext-ben");

        await ContractUpdateHandler(store, new BeaconConfiguration())
            .Handle(UpdateCommand(contract, Rule(changeTheWaitingRule ? CountSql + " WHERE 1 = 1" : CountSql), Volume()), CancellationToken.None);

        pending.Status.Should().Be(expected);
    }

    // A Custom SQL rule added to the contract after the save checked it (so no requester was resolved): under the workflow
    // the save is refused, and nothing is written, rather than saved with nobody to review it.
    [Test]
    public async Task Update_WhenTheContractGainedACustomSqlRuleMeanwhile_IsRefused_AndCommitsNothing()
    {
        var store = new ChangeApprovalStore();
        var contract = store.AddContract(ChangeApprovalStore.Volume());
        contract.Rules.Add(ChangeApprovalStore.CustomSql(CountSql, approvedSqlHash: ChangeApprovalHashes.NothingApproved));

        var act = () => ContractUpdateHandler(store, TestChangeApprovals.On()).Handle(UpdateCommand(contract, Volume()), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(UpdateDataContractHandler.RulesChangedMessage);
        store.Offline.Commits.Should().Be(0);
        contract.Rules.Should().HaveCount(2, "nothing was rewritten");
    }

    // A rejected change leaves the resource waiting with no pending request: its readers see the decision and its comment.
    [Test]
    public async Task AResourceWaitingAfterARejection_ShowsTheDecisionAndItsComment()
    {
        var store = new ChangeApprovalStore();
        var job = store.AddJob(approvedDefinitionHash: ChangeApprovalHashes.NothingApproved);
        var recipient = store.AddRecipient(Destination, ChangeApprovalHashes.NothingApproved);
        var contract = store.AddContract(ChangeApprovalStore.CustomSql(CountSql, approvedSqlHash: ChangeApprovalHashes.NothingApproved));
        Reject(store.AddRequest(ChangeApprovalKind.MigrationJob, job.Id, "a", "ext-ben"), "Copy from the replica instead.");
        Reject(store.AddRequest(ChangeApprovalKind.RecipientDestination, recipient.Id, "b", "ext-ben"), "Not an approved vendor.");
        Reject(store.AddRequest(ChangeApprovalKind.CustomSqlRules, contract.Id, "c", "ext-ben"), "Use the shared view.");
        await using var context = await store.Factory().CreateDbContextAsync(CancellationToken.None);

        var jobs = await TestChangeApprovals.MigrationJobs(TestChangeApprovals.On()).AwaitingAsync(context, [job.Id], CancellationToken.None);
        var recipients = await new GetRecipientsHandler(
                store.Factory(),
                TestPermissions.None,
                NotificationTestKit.Editor(),
                TestChangeApprovals.Deliveries(store.Factory(), store.Protector, TestChangeApprovals.On()),
                store.Protector,
                TestChangeApprovals.Hasher)
            .Handle(new GetRecipientsQuery(), CancellationToken.None);
        var detail = await new GetDataContractDetailHandler(store.Factory(), TestChangeApprovals.On()).Handle(new GetDataContractDetailQuery(contract.Id), CancellationToken.None);

        jobs[job.Id].Should().BeEquivalentTo(new { PendingRequestId = (int?)null, LastDecision = new { Status = ApprovalStatus.Rejected, Comment = "Copy from the replica instead." } });
        recipients.Items.Single().Should().BeEquivalentTo(
            new { AwaitingApproval = true, PendingApprovalRequestId = (int?)null, LastApprovalDecision = new { Status = ApprovalStatus.Rejected, Comment = "Not an approved vendor." } },
            x => x.ExcludingMissingMembers());
        detail.Should().BeEquivalentTo(
            new { PendingApprovalRequestId = (int?)null, LastApprovalDecision = new { Status = ApprovalStatus.Rejected, Comment = "Use the shared view." } },
            x => x.ExcludingMissingMembers());
    }

    // A reader who is no Approver sees the change requests of the migration jobs and data contracts it reads, never a
    // recipient's, nor one whose resource is gone; an Approver sees every one.
    [Test]
    public async Task AReaderWhoIsNoApprover_SeesTheChangesOfJobsAndContracts_NeverARecipients()
    {
        var store = new ChangeApprovalStore();
        var job = store.AddJob();
        var contract = store.AddContract(ChangeApprovalStore.Volume());
        var recipient = store.AddRecipient();
        var jobRequest = store.AddRequest(ChangeApprovalKind.MigrationJob, job.Id, "a", "ext-ben");
        var contractRequest = store.AddRequest(ChangeApprovalKind.CustomSqlRules, contract.Id, "b", "ext-ben");
        var recipientRequest = store.AddRequest(ChangeApprovalKind.RecipientDestination, recipient.Id, "c", "ext-ben");
        store.AddRequest(ChangeApprovalKind.MigrationJob, 404, "d", "ext-ben");
        var reader = new ChangeApprovalScope(ProjectSet.All, IsApprover: false);

        var read = await Approvals(store).GetPendingAsync(new GetChangeApprovalsQuery(), null, reader, CancellationToken.None);
        var all = await Approvals(store).GetPendingAsync(new GetChangeApprovalsQuery(), null, ChangeApprovalScope.Approver, CancellationToken.None);
        var recipientDetail = await Approvals(store).GetDetailAsync(recipientRequest.Id, reader, CancellationToken.None);
        var jobDetail = await Approvals(store).GetDetailAsync(jobRequest.Id, reader, CancellationToken.None);

        read.Items.Select(x => x.Id).Should().BeEquivalentTo([jobRequest.Id, contractRequest.Id]);
        all.TotalCount.Should().Be(4);
        recipientDetail.Should().BeNull("a recipient's change is an Approver's to read");
        jobDetail.Should().NotBeNull();
    }

    // Restricted to its projects, the reader's list and detail are filtered in the SQL, by the jobs' source and destination
    // and the contracts' data source, on both providers; an Approver's are not.
    [TestCase(false, "beacon.project_data_sources", "beacon.migration_jobs", "beacon.data_contracts")]
    [TestCase(true, "[beacon].[ProjectDataSources]", "[beacon].[MigrationJobs]", "[beacon].[DataContracts]")]
    public async Task AReadersListAndDetail_AreScopedInTheSql_Translates(bool sqlServer, string links, string jobs, string contracts)
    {
        var restricted = new ChangeApprovalScope(TestProjectAccess.ReadableIn(1).For(ProjectAction.Read), IsApprover: false);
        var list = Capture(sqlServer).AnsweringCountsWithOne();
        var detail = Capture(sqlServer).ThenNoRows();
        var approver = Capture(sqlServer).AnsweringCountsWithOne();

        await Approvals(list).GetPendingAsync(new GetChangeApprovalsQuery(), null, restricted, CancellationToken.None);
        var found = await Approvals(detail).GetDetailAsync(5, restricted, CancellationToken.None);
        await Approvals(approver).GetPendingAsync(new GetChangeApprovalsQuery(), null, ChangeApprovalScope.Approver, CancellationToken.None);

        found.Should().BeNull();
        list.Commands.Should().HaveCount(2).And.OnlyContain(x => x.Contains(links) && x.Contains(jobs) && x.Contains(contracts));
        detail.Commands.Should().ContainSingle().Which.Should().Contain(links).And.Contain(jobs).And.Contain(contracts);
        approver.Commands.Should().OnlyContain(x => !x.Contains(jobs) && !x.Contains(links));
    }

    private static void Reject(ChangeApprovalRequest request, string comment)
    {
        request.Status = ApprovalStatus.Rejected;
        request.ReviewComment = comment;
        request.ReviewedAt = DateTime.UtcNow;
    }

    private static string SqlHash(string sql) => CustomSqlRuleDefinition.Hash(ChangeApprovalStore.Warehouse, JsonSerializer.Serialize(new { sql }));

    private static DataContractRuleData Rule(string sql) =>
        new()
        {
            Name = "custom",
            RuleType = DataContractRuleType.CustomSql,
            Configuration = JsonSerializer.Serialize(new { sql }),
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

    private static CreateDataContractCommand CreateCommand(List<int> recipientIds, params DataContractRuleData[] rules) =>
        new(ChangeApprovalStore.Warehouse, "public", "orders", "orders contract", null, "0 * * * *", false, false, 80, [.. rules], recipientIds);

    private static UpdateDataContractCommand UpdateCommand(DataContract contract, params DataContractRuleData[] rules) =>
        new(contract.Id, ChangeApprovalStore.Warehouse, "public", "orders", contract.Name, null, contract.CronExpression, false, false, 80, [.. rules]);

    private static IBeaconActorAccessor Admin() => TestChangeApprovals.Caller(Ada, beaconUserId: 7, isAdmin: true);

    private static CreateDataContractHandler CreateHandler(ChangeApprovalStore store, bool trackContracts) =>
        new(
            store.Factory(trackContracts),
            Mock.Of<IBeaconScheduler>(),
            NotificationTestKit.UserContext(NotificationTestKit.Admin()),
            TestSqlGate.Create(),
            TestConfiguredDatabases.None,
            Admin(),
            TestChangeApprovals.On(),
            TestChangeApprovals.CustomSql(TestChangeApprovals.On(), Admin()));

    private static UpdateDataContractHandler ContractUpdateHandler(ChangeApprovalStore store, BeaconConfiguration configuration) =>
        new(
            store.Factory(),
            Mock.Of<IBeaconScheduler>(),
            NotificationTestKit.UserContext(NotificationTestKit.Admin()),
            TestSqlGate.Create(),
            TestConfiguredDatabases.None,
            Admin(),
            TestChangeApprovals.CustomSql(configuration, Admin()),
            NullLogger<UpdateDataContractHandler>.Instance);

    private static UpdateRecipientHandler RecipientUpdateHandler(ChangeApprovalStore store, BeaconConfiguration configuration, IApprovalNotifier notifier) =>
        new(
            store.Factory(),
            new RecipientSecretEditor(NotificationTestKit.Policy(), store.Protector),
            TestProjectAccess.Evaluator(TestProjectAccess.Unrestricted),
            TestProjectAccess.Actors(),
            TestPermissions.Admin,
            TestChangeApprovals.Recipients(store.Protector, configuration, TestChangeApprovals.Caller(Ada), notifier),
            NullLogger<UpdateRecipientHandler>.Instance);

    private static ChangeApprovalService Approvals(ChangeApprovalStore store) => Approvals(store.Factory(), store.Protector);

    private static ChangeApprovalService Approvals(SqlCapture capture) => Approvals(capture.Factory(), new ChangeApprovalStore().Protector);

    private static ChangeApprovalService Approvals(Microsoft.EntityFrameworkCore.IDbContextFactory<Beacon.Core.Data.BeaconContext> factory, RecipientSecretProtector protector) =>
        new(
            factory,
            TestChangeApprovals.Caller("ext-cid"),
            Mock.Of<IBeaconUserContext>(),
            protector,
            TestChangeApprovals.Hasher,
            TestChangeApprovals.On(),
            NullLogger<ChangeApprovalService>.Instance);

    private static SqlCapture Capture(bool sqlServer) => sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture();
}

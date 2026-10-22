using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.DataMigration;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Approvals;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// Deciding a change of a migration job, a Custom SQL rule or a recipient, as query changes are decided: four eyes (the
/// requester never approves their own change, whichever way either signed in; a request without a recorded requester is
/// only rejected), decisions only from Pending, approval only of the payload the reviewer was shown and only while the
/// resource still holds exactly the definition the request records, the resource's row taken first and everything
/// committed together.
/// </summary>
[TestFixture]
public class ChangeApprovalDecisionTests
{
    private const string Ada = "ext-ada";

    // The requester under every id their request may present: the external id, another case of it, a session id earlier
    // versions recorded, and an API key owner's numeric Users.Id.
    [TestCase("ext-ada", 9, null)]
    [TestCase("EXT-ADA", 9, null)]
    [TestCase("session-ada", 9, "session-ada")]
    [TestCase("3", 3, null)]
    public async Task Approve_ByItsRequester_IsRefusedUnderEveryIdentityForm_AndChangesNothing(string recordedAs, int beaconUserId, string? sessionId)
    {
        var (store, job, request) = PendingJob(requestedBy: recordedAs);
        var reviewer = TestChangeApprovals.Caller(Ada, beaconUserId, sessionId == null ? null : [sessionId]);

        var act = () => Service(store, reviewer).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(QueryApprovalService.OwnRequestMessage);
        ShouldBeUntouched(store, job, request);
    }

    [Test]
    public async Task ARequestWithoutARecordedRequester_IsRejectedButNeverApproved()
    {
        var (store, job, request) = PendingJob(requestedBy: null);

        var approve = () => Service(store).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        await approve.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(QueryApprovalService.NoRequesterMessage);
        ShouldBeUntouched(store, job, request);

        var decision = await Service(store).RejectAsync(request.Id, "submit again", CancellationToken.None);

        decision.Status.Should().Be(ApprovalStatus.Rejected);
        request.Status.Should().Be(ApprovalStatus.Rejected);
        job.ApprovedDefinitionHash.Should().Be(ChangeApprovalHashes.NothingApproved, "a rejected job still does not run");
    }

    [Test]
    public async Task Approve_ByTheRequester_WhenTheHostAllowsSelfApproval_Applies()
    {
        var (store, job, request) = PendingJob(requestedBy: Ada);

        await Service(store, TestChangeApprovals.Caller(Ada), TestChangeApprovals.On(allowSelfApproval: true))
            .ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        request.Status.Should().Be(ApprovalStatus.Approved);
        job.ApprovedDefinitionHash.Should().Be(MigrationJobDefinition.Of(job).Hash);
    }

    [Test]
    public async Task Approve_BySomeoneElse_RecordsTheDecision_AndTheJobsApprovedDefinition_InOneCommit()
    {
        var (store, job, request) = PendingJob(requestedBy: "ext-ben");
        store.User(7, Ada, "Ada Lovelace");
        var notifier = new Mock<IApprovalNotifier>();

        var decision = await Service(store, notifier: notifier.Object).ApproveAsync(request.Id, "looks right", request.PayloadHash, CancellationToken.None);

        decision.Should().Be(new ChangeApprovalDecision(request.Id, ApprovalStatus.Approved, "ext-ben", Ada));
        request.Should().BeEquivalentTo(
            new { Status = ApprovalStatus.Approved, ReviewedByUserId = Ada, ReviewedByUserName = "Ada Lovelace", ReviewComment = "looks right" },
            x => x.ExcludingMissingMembers());
        request.ReviewedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        job.ApprovedDefinitionHash.Should().Be(request.DefinitionHash);
        store.Offline.Commits.Should().Be(1);
        notifier.Verify(x => x.ApprovalUpdatedAsync(request.Id, "approved", Ada, "ext-ben", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestCase(ApprovalStatus.Approved)]
    [TestCase(ApprovalStatus.Rejected)]
    [TestCase(ApprovalStatus.Superseded)]
    public async Task ADecisionOnARequestThatIsNoLongerPending_Is409(ApprovalStatus status)
    {
        var (store, job, request) = PendingJob(requestedBy: "ext-ben");
        request.Status = status;
        request.ReviewedByUserId = "ext-cid";

        var approve = () => Service(store).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);
        var reject = () => Service(store).RejectAsync(request.Id, null, CancellationToken.None);

        await approve.Should().ThrowAsync<BeaconConflictException>().WithMessage(QueryApprovalService.AlreadyDecidedMessage);
        await reject.Should().ThrowAsync<BeaconConflictException>().WithMessage(QueryApprovalService.AlreadyDecidedMessage);
        request.Status.Should().Be(status, "a decision is never overwritten");
        request.ReviewedByUserId.Should().Be("ext-cid");
        job.ApprovedDefinitionHash.Should().Be(ChangeApprovalHashes.NothingApproved);
        store.Offline.Commits.Should().Be(0);
    }

    // The approval names the payload the reviewer was shown; one that names nothing, or another payload, applies nothing.
    [TestCase(null, "An approval names the content it approves*")]
    [TestCase("", "An approval names the content it approves*")]
    [TestCase("0000000000000000000000000000000000000000000000000000000000000000", "The proposed change is not the content you reviewed*")]
    public async Task Approve_OfAnotherPayloadThanTheReviewerWasShown_Is409(string? expected, string message)
    {
        var (store, job, request) = PendingJob(requestedBy: "ext-ben");

        var act = () => Service(store).ApproveAsync(request.Id, null, expected, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(message);
        ShouldBeUntouched(store, job, request);
    }

    // The job's definition changed after the request (outside the workflow): approving would approve something nobody saw.
    [Test]
    public async Task Approve_WhenTheResourceNoLongerHoldsTheRequestedDefinition_Is409()
    {
        var (store, job, request) = PendingJob(requestedBy: "ext-ben");
        job.QueryText = "SELECT * FROM salaries";

        var act = () => Service(store).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(ChangeApprovalService.ChangedSinceSubmittedMessage);
        ShouldBeUntouched(store, job, request);
    }

    [Test]
    public async Task Approve_OfAResourceThatIsGone_Is409_ButItCanBeRejected()
    {
        var (store, job, request) = PendingJob(requestedBy: "ext-ben");
        store.Jobs.Remove(job);

        var approve = () => Service(store).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        await approve.Should().ThrowAsync<BeaconConflictException>().WithMessage(ChangeApprovalService.ResourceGoneMessage);
        request.Status.Should().Be(ApprovalStatus.Pending);

        await Service(store).RejectAsync(request.Id, "job deleted", CancellationToken.None);

        request.Status.Should().Be(ApprovalStatus.Rejected);
    }

    [Test]
    public async Task Reject_ByItsRequester_WithdrawsTheChange_AndTheJobStillDoesNotRun()
    {
        var (store, job, request) = PendingJob(requestedBy: Ada);

        var decision = await Service(store).RejectAsync(request.Id, "withdrawn", CancellationToken.None);

        decision.Should().Be(new ChangeApprovalDecision(request.Id, ApprovalStatus.Rejected, Ada, Ada));
        request.Status.Should().Be(ApprovalStatus.Rejected);
        job.ApprovedDefinitionHash.Should().Be(ChangeApprovalHashes.NothingApproved);
        store.Offline.Commits.Should().Be(1);
    }

    [Test]
    public async Task ADecision_ByACallerWithoutAUserId_IsRefused()
    {
        var (store, job, request) = PendingJob(requestedBy: "ext-ben");
        var nobody = TestChangeApprovals.Caller(null);

        var approve = () => Service(store, nobody).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);
        var reject = () => Service(store, nobody).RejectAsync(request.Id, null, CancellationToken.None);

        await approve.Should().ThrowAsync<BeaconForbiddenException>();
        await reject.Should().ThrowAsync<BeaconForbiddenException>();
        ShouldBeUntouched(store, job, request);
    }

    [Test]
    public async Task ADecision_OnARequestThatDoesNotExist_IsNotFound()
    {
        var store = new ChangeApprovalStore();

        var approve = () => Service(store).ApproveAsync(404, null, "x", CancellationToken.None);
        var reject = () => Service(store).RejectAsync(404, null, CancellationToken.None);

        await approve.Should().ThrowAsync<BeaconNotFoundException>();
        await reject.Should().ThrowAsync<BeaconNotFoundException>();
    }

    // The detail says who may approve, and why not: the server decides again on approval.
    [Test]
    public async Task TheDetail_OfTheCallersOwnRequest_ShowsItBlockedWithTheReason()
    {
        var (store, _, request) = PendingJob(requestedBy: Ada);

        var detail = await Service(store).GetDetailAsync(request.Id, ChangeApprovalScope.Approver, CancellationToken.None);

        detail!.Should().BeEquivalentTo(
            new { IsOwnRequest = true, MayApprove = false, ApproveBlockedReason = QueryApprovalService.OwnRequestMessage, IsStale = false, PayloadHash = request.PayloadHash },
            x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task TheDetail_OfADecidedRequest_NamesTheReviewerByUsersId()
    {
        var (store, _, request) = PendingJob(requestedBy: "ext-ben");
        store.User(3, Ada, "Ada Lovelace");
        request.Status = ApprovalStatus.Approved;
        request.ReviewedByUserId = Ada;
        request.ReviewedByUserName = "Ada Lovelace";

        var detail = await Service(store).GetDetailAsync(request.Id, ChangeApprovalScope.Approver, CancellationToken.None);

        detail!.ReviewedByBeaconUserId.Should().Be(3);
    }

    [Test]
    public async Task TheDetail_ShowsTheProposedJob_WithItsDataSourceNames_AndNoOtherKind()
    {
        var store = new ChangeApprovalStore();
        var job = store.AddJob("SELECT id FROM orders", ChangeApprovalHashes.NothingApproved);
        var view = MigrationJobDefinition.Of(job).View();
        var request = store.AddRequest(ChangeApprovalKind.MigrationJob, job.Id, MigrationJobDefinition.Of(job).Hash, "ext-ben", proposedJson: Json(view));

        var detail = await Service(store).GetDetailAsync(request.Id, ChangeApprovalScope.Approver, CancellationToken.None);

        detail!.ProposedMigrationJob.Should().BeEquivalentTo(new
        {
            SourceDataSourceId = ChangeApprovalStore.Warehouse,
            SourceDataSourceName = "warehouse",
            QueryText = "SELECT id FROM orders",
            DestinationDataSourceId = ChangeApprovalStore.Target,
            DestinationDataSourceName = "target",
            DestinationTable = "orders",
            Mode = MigrationMode.Insert
        });
        detail.PreviousMigrationJob.Should().BeNull("the change created the job");
        detail.ProposedCustomSqlRules.Should().BeNull();
        detail.ProposedRecipientDestination.Should().BeNull();
        detail.MayApprove.Should().BeTrue();
        detail.ResourceExists.Should().BeTrue();
    }

    [Test]
    public async Task TheDetail_OfAResourceChangedSinceTheRequest_IsStale_AndCannotBeApproved()
    {
        var (store, job, request) = PendingJob(requestedBy: "ext-ben");
        job.DestinationTable = "salaries";

        var detail = await Service(store).GetDetailAsync(request.Id, ChangeApprovalScope.Approver, CancellationToken.None);

        detail!.IsStale.Should().BeTrue();
        detail.MayApprove.Should().BeFalse();
        detail.ApproveBlockedReason.Should().Be(ChangeApprovalService.ChangedSinceSubmittedMessage);
    }

    [Test]
    public async Task TheList_HoldsThePendingRequests_OfOneKindWhenAsked()
    {
        var store = new ChangeApprovalStore();
        var job = store.AddJob();
        var recipient = store.AddRecipient();
        store.AddRequest(ChangeApprovalKind.MigrationJob, job.Id, "a", "ext-ben");
        store.AddRequest(ChangeApprovalKind.RecipientDestination, recipient.Id, "b", "ext-ben");
        store.AddRequest(ChangeApprovalKind.RecipientDestination, recipient.Id, "c", "ext-ben", ApprovalStatus.Approved);

        var all = await Service(store).GetPendingAsync(new Beacon.Core.Handlers.Approvals.GetChangeApprovalsQuery(), null, ChangeApprovalScope.Approver, CancellationToken.None);
        var recipients = await Service(store).GetPendingAsync(new Beacon.Core.Handlers.Approvals.GetChangeApprovalsQuery(), ChangeApprovalKind.RecipientDestination, ChangeApprovalScope.Approver, CancellationToken.None);

        all.TotalCount.Should().Be(2);
        recipients.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { Kind = ChangeApprovalKind.RecipientDestination, ResourceId = recipient.Id, Status = ApprovalStatus.Pending },
            x => x.ExcludingMissingMembers());
    }

    // The same rules for every kind: the requester under any identity form never approves, a decided request is not
    // decided again, and an approval names the payload the reviewer was shown.
    [TestCase(ChangeApprovalKind.MigrationJob)]
    [TestCase(ChangeApprovalKind.CustomSqlRules)]
    [TestCase(ChangeApprovalKind.RecipientDestination)]
    public async Task EveryKind_IsApprovedByAnotherPerson_OnceAndForWhatTheyWereShown(ChangeApprovalKind kind)
    {
        var (store, request, isApproved) = PendingOf(kind, requestedBy: "session-ada");
        var service = Service(store);

        var byRequester = () => Service(store, TestChangeApprovals.Caller("ext-x", sessionIds: ["session-ada"])).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);
        var otherPayload = () => service.ApproveAsync(request.Id, null, "another-payload", CancellationToken.None);

        await byRequester.Should().ThrowAsync<BeaconForbiddenException>();
        await otherPayload.Should().ThrowAsync<BeaconConflictException>().WithMessage(QueryApprovalService.NotTheReviewedContentMessage);
        isApproved().Should().BeFalse();

        await service.ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);
        var again = () => service.ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        isApproved().Should().BeTrue();
        await again.Should().ThrowAsync<BeaconConflictException>().WithMessage(QueryApprovalService.AlreadyDecidedMessage);
        store.Offline.Commits.Should().Be(1);
    }

    // A pending request of a resource of the kind, and whether the resource now works with what it proposes.
    private static (ChangeApprovalStore Store, ChangeApprovalRequest Request, Func<bool> IsApproved) PendingOf(ChangeApprovalKind kind, string requestedBy)
    {
        var store = new ChangeApprovalStore();
        switch (kind)
        {
            case ChangeApprovalKind.MigrationJob:
                {
                    var job = store.AddJob(approvedDefinitionHash: ChangeApprovalHashes.NothingApproved);
                    var request = store.AddRequest(kind, job.Id, MigrationJobDefinition.Of(job).Hash, requestedBy);

                    return (store, request, () => ChangeApprovalHashes.Allows(job.ApprovedDefinitionHash, MigrationJobDefinition.Of(job).Hash));
                }

            case ChangeApprovalKind.CustomSqlRules:
                {
                    var rule = ChangeApprovalStore.CustomSql("SELECT 1 AS passed", approvedSqlHash: ChangeApprovalHashes.NothingApproved);
                    var contract = store.AddContract(rule);
                    var request = store.AddRequest(kind, contract.Id, CustomSqlRuleDefinition.SetHash([CustomSqlRuleDefinition.Hash(contract.DataSourceId, rule.Configuration)]), requestedBy);

                    return (store, request, () => !CustomSqlRuleDefinition.Awaits(rule, contract.DataSourceId));
                }

            default:
                {
                    var recipient = store.AddRecipient("https://hooks.example.com/in", ChangeApprovalHashes.NothingApproved);
                    var request = store.AddRequest(kind, recipient.Id, ChangeApprovalStore.HookHash("https://hooks.example.com/in"), requestedBy);

                    return (store, request, () => recipient.ApprovedDestinationHash == ChangeApprovalStore.HookHash("https://hooks.example.com/in"));
                }
        }
    }

    private static (ChangeApprovalStore Store, MigrationJob Job, ChangeApprovalRequest Request) PendingJob(string? requestedBy)
    {
        var store = new ChangeApprovalStore();
        var job = store.AddJob(approvedDefinitionHash: ChangeApprovalHashes.NothingApproved);
        var definition = MigrationJobDefinition.Of(job);
        var request = store.AddRequest(ChangeApprovalKind.MigrationJob, job.Id, definition.Hash, requestedBy, proposedJson: Json(definition.View()));

        return (store, job, request);
    }

    private static void ShouldBeUntouched(ChangeApprovalStore store, MigrationJob job, ChangeApprovalRequest request)
    {
        request.Status.Should().Be(ApprovalStatus.Pending);
        request.ReviewedByUserId.Should().BeNull();
        job.ApprovedDefinitionHash.Should().Be(ChangeApprovalHashes.NothingApproved);
        store.Offline.Commits.Should().Be(0);
    }

    private static string Json(object view) =>
        System.Text.Json.JsonSerializer.Serialize(view, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

    private static ChangeApprovalService Service(
        ChangeApprovalStore store,
        IBeaconActorAccessor? reviewer = null,
        BeaconConfiguration? configuration = null,
        IApprovalNotifier? notifier = null) =>
        new(
            store.Factory(),
            reviewer ?? TestChangeApprovals.Caller(Ada, beaconUserId: 7),
            Mock.Of<IBeaconUserContext>(),
            store.Protector,
            TestChangeApprovals.Hasher,
            configuration ?? TestChangeApprovals.On(),
            new RecordingSecurityAudit(),
            NullLogger<ChangeApprovalService>.Instance,
            notifier);
}

using System.Text.Json;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// Approving and rejecting a query change: four eyes (the requester never approves their own change, whichever way either
/// signed in; a request without a recorded requester is only rejected), decisions only from Pending, approval only on top
/// of the version the change was made on and for exactly the content submitted, every step checked again, and the
/// decision, the query's move and its steps applied in one transaction.
/// </summary>
[TestFixture]
public class ApprovalDecisionTests
{
    private const string Ada = "ext-ada";

    [Test]
    public async Task Approve_ByItsRequester_IsRefused_AndChangesNothing()
    {
        var (store, active, proposal, request) = Pending(requestedBy: Ada);

        var act = () => Service(store).ApproveAsync(request.Id, null, Shown(request), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        ShouldBeUntouched(store, active, proposal, request);
    }

    // An API key's submission recorded by earlier versions names its owner by the numeric Users.Id the key presents; the
    // owner's own session must still not approve it.
    [Test]
    public async Task Approve_OfARequestRecordedByTheReviewersApiKey_IsRefused()
    {
        var (store, active, proposal, request) = Pending(requestedBy: "3");

        var act = () => Service(store, Reviewer(Ada, beaconUserId: 3)).ApproveAsync(request.Id, null, Shown(request), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        ShouldBeUntouched(store, active, proposal, request);
    }

    // Earlier versions recorded a session's own id claim; it names the reviewer as well, whatever its case.
    [TestCase("session-ada")]
    [TestCase("Session-ADA")]
    public async Task Approve_OfALegacyRequestRecordedByTheReviewersSessionId_IsRefused(string requestedBy)
    {
        var (store, active, proposal, request) = Pending(requestedBy: requestedBy);

        var act = () => Service(store, Reviewer(Ada, sessionIds: ["session-ada"])).ApproveAsync(request.Id, null, Shown(request), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        ShouldBeUntouched(store, active, proposal, request);
    }

    [Test]
    public async Task ARequestWithoutARecordedRequester_IsRejectedButNeverApproved()
    {
        var (store, active, proposal, request) = Pending(requestedBy: null);

        var approve = () => Service(store, Reviewer("ext-ben")).ApproveAsync(request.Id, null, Shown(request), CancellationToken.None);

        await approve.Should().ThrowAsync<BeaconForbiddenException>();
        ShouldBeUntouched(store, active, proposal, request);

        var decision = await Service(store, Reviewer("ext-ben")).RejectAsync(request.Id, "resubmit", CancellationToken.None);

        decision.Status.Should().Be(ApprovalStatus.Rejected);
        request.Status.Should().Be(ApprovalStatus.Rejected);
        proposal.Status.Should().Be(QueryVersionStatus.Rejected);
    }

    [Test]
    public async Task Approve_ByTheRequester_WhenTheHostAllowsSelfApproval_Applies()
    {
        var (store, _, proposal, request) = Pending(requestedBy: Ada);
        var selfApproval = new BeaconConfiguration { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true, AllowSelfApproval = true } };

        await Service(store, configuration: selfApproval).ApproveAsync(request.Id, null, Shown(request), CancellationToken.None);

        request.Status.Should().Be(ApprovalStatus.Approved);
        store.Query.ActiveVersionId.Should().Be(proposal.Id);
    }

    [TestCase(ApprovalStatus.Approved)]
    [TestCase(ApprovalStatus.Rejected)]
    [TestCase(ApprovalStatus.Superseded)]
    public async Task ADecisionOnARequestThatIsNoLongerPending_Is409(ApprovalStatus status)
    {
        var (store, active, proposal, request) = Pending(requestedBy: "ext-ben");
        request.Status = status;
        request.ReviewedByUserId = "ext-cid";

        var approve = () => Service(store).ApproveAsync(request.Id, null, Shown(request), CancellationToken.None);
        var reject = () => Service(store).RejectAsync(request.Id, null, CancellationToken.None);

        await approve.Should().ThrowAsync<BeaconConflictException>().WithMessage(QueryApprovalService.AlreadyDecidedMessage);
        await reject.Should().ThrowAsync<BeaconConflictException>().WithMessage(QueryApprovalService.AlreadyDecidedMessage);
        request.Status.Should().Be(status, "a decision is never overwritten");
        request.ReviewedByUserId.Should().Be("ext-cid");
        store.Query.ActiveVersionId.Should().Be(active.Id);
        proposal.Status.Should().Be(QueryVersionStatus.PendingApproval);
    }

    [Test]
    public async Task Approve_WhenAnotherVersionWentLiveSinceTheRequest_IsStale()
    {
        var (store, active, proposal, request) = Pending(requestedBy: "ext-ben");
        var newer = store.AddVersion(3, QueryVersionStatus.Active, "SELECT 3");
        active.Status = QueryVersionStatus.Archived;
        store.Activate(newer);

        var act = () => Service(store).ApproveAsync(request.Id, null, Shown(request), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(QueryApprovalService.StaleMessage);
        request.Status.Should().Be(ApprovalStatus.Pending);
        store.Query.ActiveVersionId.Should().Be(newer.Id);
        proposal.Status.Should().Be(QueryVersionStatus.PendingApproval);
    }

    // A request recorded before Beacon kept a change's base and content can neither be told stale nor bound to what was
    // submitted: it is rejected, and the change submitted again.
    [Test]
    public async Task ALegacyRequestWithoutABaseOrAHash_IsRejectedButNeverApproved()
    {
        var store = new GovernanceStore();
        var active = store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var proposal = store.AddVersion(2, QueryVersionStatus.PendingApproval, "SELECT 2", legacy: true);
        var request = store.AddRequest(proposal, "ext-ben", baseVersionId: null);

        var approve = () => Service(store).ApproveAsync(request.Id, null, Shown(request), CancellationToken.None);

        await approve.Should().ThrowAsync<BeaconConflictException>().WithMessage(QueryApprovalService.LegacyRequestMessage);
        ShouldBeUntouched(store, active, proposal, request);

        await Service(store).RejectAsync(request.Id, "submit again", CancellationToken.None);

        request.Status.Should().Be(ApprovalStatus.Rejected);
        proposal.Status.Should().Be(QueryVersionStatus.Rejected);
    }

    // A change submitted while the query had no active version is made on none: approved while that holds, stale once a
    // version went live.
    [TestCase(false)]
    [TestCase(true)]
    public async Task ARequestMadeOnNoActiveVersion_IsApprovedOnlyWhileNoneIsActive(bool aVersionWentLive)
    {
        var store = new GovernanceStore();
        var proposal = store.AddVersion(1, QueryVersionStatus.PendingApproval, "SELECT 2");
        var request = store.AddRequest(proposal, "ext-ben", baseVersionId: null);
        if (aVersionWentLive)
        {
            store.Activate(store.AddVersion(2, QueryVersionStatus.Active));
        }

        var act = () => Service(store).ApproveAsync(request.Id, null, Shown(request), CancellationToken.None);

        if (aVersionWentLive)
        {
            await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(QueryApprovalService.StaleMessage);
            request.Status.Should().Be(ApprovalStatus.Pending);
            return;
        }

        await act.Should().NotThrowAsync();
        store.Query.ActiveVersionId.Should().Be(proposal.Id);
    }

    // An identity provider may change the case of a subject it issues: the requester is still the requester.
    [Test]
    public async Task Approve_ByTheRequesterUnderAnotherCaseOfTheirId_IsRefused()
    {
        var (store, active, proposal, request) = Pending(requestedBy: "EXT-ADA");

        var act = () => Service(store).ApproveAsync(request.Id, null, Shown(request), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        ShouldBeUntouched(store, active, proposal, request);
    }

    // An AI actor's change made by approving its plan records the plan's reviewer and its revision requesters: none of them
    // approves the change, whichever case their id is recorded in; anyone else may.
    [TestCase("ext-ada", false)]
    [TestCase("Ext-Ada", false)]
    [TestCase("ext-cid", true)]
    public async Task Approve_ByWhoeverShapedAnAiChange_IsRefused(string contributor, bool allowed)
    {
        var (store, active, proposal, request) = Pending(requestedBy: "ext-ana");
        request.ContributorUserIds = ["ext-ben", contributor];

        var act = () => Service(store).ApproveAsync(request.Id, null, Shown(request), CancellationToken.None);

        if (allowed)
        {
            await act.Should().NotThrowAsync();
            request.Status.Should().Be(ApprovalStatus.Approved);
            return;
        }

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        ShouldBeUntouched(store, active, proposal, request);
    }

    [Test]
    public async Task Approve_ByWhoeverShapedAnAiChange_WhenTheHostAllowsSelfApproval_Applies()
    {
        var (store, _, proposal, request) = Pending(requestedBy: "ext-ana");
        request.ContributorUserIds = [Ada];
        var selfApproval = new BeaconConfiguration { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true, AllowSelfApproval = true } };

        await Service(store, configuration: selfApproval).ApproveAsync(request.Id, null, Shown(request), CancellationToken.None);

        store.Query.ActiveVersionId.Should().Be(proposal.Id);
    }

    // A reviewer's step diff pairs steps by their place in the run, not by the order value alone: two steps sharing an order
    // (data written before orders were checked) are both shown, and a swap shows as two changed steps.
    [Test]
    public async Task TheStepDiff_PairsStepsByTheirPlaceInTheRun()
    {
        var store = new GovernanceStore();
        var active = store.AddVersion(1, QueryVersionStatus.Active);
        active.StepsJson = Json([GovernanceStore.Snapshot("SELECT 1"), GovernanceStore.Snapshot("SELECT 2", 2, "Step 2")]);
        var duplicated = store.AddVersion(2, QueryVersionStatus.PendingApproval);
        duplicated.StepsJson = Json([GovernanceStore.Snapshot("SELECT 1"), GovernanceStore.Snapshot("SELECT 3", 1, "Step 2")]);
        var swapped = store.AddVersion(3, QueryVersionStatus.PendingApproval);
        swapped.StepsJson = Json([GovernanceStore.Snapshot("SELECT 2"), GovernanceStore.Snapshot("SELECT 1", 2, "Step 2")]);
        var service = TestQueryServices.VersionService(store.Factory());

        var withDuplicate = await service.DiffVersionsAsync(active.Id, duplicated.Id, CancellationToken.None);
        var withSwap = await service.DiffVersionsAsync(active.Id, swapped.Id, CancellationToken.None);

        withDuplicate.StepDiffs.Select(x => x.StepB?.SqlValue).Should().Equal("SELECT 1", "SELECT 3");
        withDuplicate.StepDiffs.Select(x => x.DiffType).Should().Equal(StepDiffType.Unchanged, StepDiffType.Modified);
        withSwap.StepDiffs.Select(x => x.DiffType).Should().Equal(StepDiffType.Modified, StepDiffType.Modified);
    }

    // Steps sharing an order leave the run order, and the review's step diff, to chance: such a proposal (recorded before
    // orders were checked) is not applied.
    [Test]
    public async Task Approve_OfAProposalWhoseStepOrdersDoNotRunInSequence_IsRefused()
    {
        var store = new GovernanceStore();
        var active = store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var steps = new List<QueryStepSnapshot> { GovernanceStore.Snapshot("SELECT 2"), GovernanceStore.Snapshot("SELECT 3", 1, "Step 2") };
        var proposal = store.AddVersion(2, QueryVersionStatus.PendingApproval);
        proposal.StepsJson = Json(steps);
        proposal.ContentHash = QueryContentHash.Of(null, steps).Hash;
        var request = store.AddRequest(proposal, "ext-ben", active.Id);

        var act = () => Service(store).ApproveAsync(request.Id, null, Shown(request), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage($"*{QueryStepOrders.Refusal}");
        ShouldBeUntouched(store, active, proposal, request);
    }

    [Test]
    public async Task Approve_OfAProposalWhoseContentNoLongerMatchesItsHash_IsRefused()
    {
        var (store, active, proposal, request) = Pending(requestedBy: "ext-ben");
        proposal.ContentHash = new string('0', 64);

        var act = () => Service(store).ApproveAsync(request.Id, null, Shown(request), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage("*no longer holds the content it was submitted with*");
        ShouldBeUntouched(store, active, proposal, request);
    }

    [Test]
    public async Task Approve_ChecksEveryStepAgain_AndAStepThatNoLongerPassesIsRefused()
    {
        var (store, active, proposal, request) = Pending(requestedBy: "ext-ben");
        var saveCheck = new Mock<IStepSqlSaveCheck>();
        saveCheck
            .Setup(x => x.RejectionAsync(GovernanceStore.DataSourceId, "SELECT 2", It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("The table orders does not exist.");

        var act = () => Service(store, saveCheck: saveCheck.Object).ApproveAsync(request.Id, null, Shown(request), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage("Step 1 cannot be approved: The table orders does not exist.");
        ShouldBeUntouched(store, active, proposal, request);
    }

    [Test]
    public async Task Approve_AppliesTheProposalInOneTransaction_AsTheCaller()
    {
        var (store, active, proposal, request) = Pending(requestedBy: "ext-ben");
        store.User(3, Ada, "Ada Lovelace");
        var oldStep = store.Query.Steps[0];

        var decision = await Service(store).ApproveAsync(request.Id, "looks right", Shown(request), CancellationToken.None);

        decision.Should().Be(new QueryApprovalDecision(request.Id, ApprovalStatus.Approved, "ext-ben", Ada));
        request.Should().BeEquivalentTo(new
        {
            Status = ApprovalStatus.Approved,
            ReviewedByUserId = Ada,
            ReviewedByUserName = "Ada Lovelace",
            ReviewComment = "looks right",
            ApprovedContentHash = proposal.ContentHash,
        }, x => x.ExcludingMissingMembers());
        store.Query.ActiveVersionId.Should().Be(proposal.Id);
        proposal.Status.Should().Be(QueryVersionStatus.Active);
        active.Status.Should().Be(QueryVersionStatus.Archived);
        store.Query.Steps.Should().NotContain(oldStep, "the live steps are replaced by the proposal's");
        store.Saved.OfType<QueryStep>().Should().ContainSingle().Which.SqlValue.Should().Be("SELECT 2");
        store.SaveCount.Should().Be(1, "one save applies the steps and the versions");
        store.Offline.TransactionIsolationLevels.Should().ContainSingle("the decision, the move and the steps commit together");
        store.Offline.Commits.Should().Be(1);
    }

    // A version without a recorded hash, on a request that records its base, is approved as it reads now, and gets its
    // hash with the approval.
    [Test]
    public async Task Approve_OfAProposalWithoutAHash_RecordsItsHash()
    {
        var store = new GovernanceStore();
        var active = store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var proposal = store.AddVersion(2, QueryVersionStatus.PendingApproval, "SELECT 2", legacy: true);
        var request = store.AddRequest(proposal, "ext-ben", baseVersionId: active.Id);

        await Service(store).ApproveAsync(request.Id, null, Shown(request), CancellationToken.None);

        proposal.ContentHash.Should().MatchRegex("^[0-9a-f]{64}$");
        request.ApprovedContentHash.Should().Be(proposal.ContentHash);
        store.Query.ActiveVersionId.Should().Be(proposal.Id);
        active.Status.Should().Be(QueryVersionStatus.Archived);
    }

    // The approval names the content the reviewer was shown: other content, or none named, is refused and changes nothing.
    [TestCase(null, QueryApprovalService.ContentNotNamedMessage)]
    [TestCase("", QueryApprovalService.ContentNotNamedMessage)]
    [TestCase("0000000000000000000000000000000000000000000000000000000000000000", QueryApprovalService.NotTheReviewedContentMessage)]
    public async Task Approve_OfOtherContentThanTheReviewerWasShown_Is409AndChangesNothing(string? shown, string message)
    {
        var (store, active, proposal, request) = Pending(requestedBy: "ext-ben");

        var act = () => Service(store).ApproveAsync(request.Id, null, shown, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(message);
        ShouldBeUntouched(store, active, proposal, request);
    }

    // The detail tells the caller what the server will decide: the content an approval names, whether the request is
    // theirs, and whether (and why not) they may approve it now.
    [TestCase("ext-ana", null, ApprovalStatus.Pending, false, true, null)]
    [TestCase(Ada, null, ApprovalStatus.Pending, false, false, QueryApprovalService.OwnRequestMessage)]
    [TestCase("ext-ana", Ada, ApprovalStatus.Pending, false, false, QueryApprovalService.ContributorMessage)]
    [TestCase(null, null, ApprovalStatus.Pending, false, false, QueryApprovalService.NoRequesterMessage)]
    [TestCase("ext-ana", null, ApprovalStatus.Pending, true, false, QueryApprovalService.StaleMessage)]
    [TestCase("ext-ana", null, ApprovalStatus.Superseded, false, false, null)]
    public async Task TheDetail_ShowsTheContentAnApprovalNames_AndWhetherTheCallerMayApprove(
        string? requestedBy,
        string? contributor,
        ApprovalStatus status,
        bool aVersionWentLive,
        bool mayApprove,
        string? blockedReason)
    {
        var (store, active, proposal, request) = Pending(requestedBy);
        request.Status = status;
        request.ContributorUserIds = contributor == null ? null : [contributor];
        if (aVersionWentLive)
        {
            active.Status = QueryVersionStatus.Archived;
            store.Activate(store.AddVersion(3, QueryVersionStatus.Active, "SELECT 3"));
        }

        var detail = await Service(store).GetApprovalDetailAsync(request.Id, CancellationToken.None);

        detail.Should().BeEquivalentTo(new
        {
            ContentHash = proposal.ContentHash,
            IsOwnRequest = requestedBy == Ada,
            MayApprove = mayApprove,
            ApproveBlockedReason = blockedReason,
            IsStale = aVersionWentLive,
        }, x => x.ExcludingMissingMembers());
    }

    // A request recorded before Beacon kept a change's base and content is reject-only; its detail says so, and names the
    // content its snapshot holds.
    [Test]
    public async Task TheDetail_OfALegacyRequest_SaysItIsRejectOnly()
    {
        var store = new GovernanceStore();
        store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var proposal = store.AddVersion(2, QueryVersionStatus.PendingApproval, "SELECT 2", legacy: true);
        var request = store.AddRequest(proposal, "ext-ben", baseVersionId: null);

        var detail = await Service(store).GetApprovalDetailAsync(request.Id, CancellationToken.None);

        detail!.MayApprove.Should().BeFalse();
        detail.ApproveBlockedReason.Should().Be(QueryApprovalService.LegacyRequestMessage);
        detail.IsStale.Should().BeFalse("a request without a base cannot be told stale");
        detail.ContentHash.Should().Be(QueryContentHash.Of(null, [GovernanceStore.Snapshot("SELECT 2")]).Hash);
    }

    // What an approval applies is exactly what was submitted: the live query's content hashes as the proposal's, with its
    // steps (names, data sources, SQL, parameter types and placeholders), its final query, name and description.
    [Test]
    public async Task Approve_LeavesTheLiveQueryWithExactlyTheApprovedContent()
    {
        var store = new GovernanceStore();
        var active = store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var steps = new List<QueryStepSnapshot>
        {
            new()
            {
                StepOrder = 1,
                SqlValue = "SELECT id FROM orders WHERE placed > {since}",
                DataSourceId = GovernanceStore.DataSourceId,
                DataSourceName = "warehouse",
                Name = "Recent orders",
                Parameters = [new QueryStepParameterSnapshot { Name = "since", Type = ParameterType.DateTime, Placeholder = "{since}" }],
            },
            new()
            {
                StepOrder = 2,
                SqlValue = "SELECT id FROM refunds",
                DataSourceId = GovernanceStore.LedgerId,
                DataSourceName = "ledger",
                Name = "Refunds",
            },
        };
        var proposal = store.AddVersion(2, QueryVersionStatus.PendingApproval, finalQuery: "SELECT * FROM step1 JOIN step2 USING (id)");
        proposal.Name = "Orders with refunds";
        proposal.Description = "Orders placed since a date, with their refunds";
        proposal.StepsJson = Json(steps);
        proposal.ContentHash = QueryContentHash.Of(proposal.FinalQuery, steps).Hash;
        var request = store.AddRequest(proposal, "ext-ben", active.Id);

        await Service(store).ApproveAsync(request.Id, null, Shown(request), CancellationToken.None);

        var live = new Query
        {
            FinalQuery = store.Query.FinalQuery,
            Steps = store.Query.Steps.Concat(store.Saved.OfType<QueryStep>()).ToList(),
        };
        QueryContentHash.Of(live).Hash.Should().Be(proposal.ContentHash);
        store.Query.Should().BeEquivalentTo(new
        {
            Name = "Orders with refunds",
            Description = "Orders placed since a date, with their refunds",
            FinalQuery = "SELECT * FROM step1 JOIN step2 USING (id)",
            ActiveVersionId = proposal.Id,
        }, x => x.ExcludingMissingMembers());
        live.Steps.Select(x => (x.StepOrder, x.Name, x.DataSourceId)).Should().Equal((1, "Recent orders", GovernanceStore.DataSourceId), (2, "Refunds", GovernanceStore.LedgerId));
        live.Steps[0].Parameters.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Name = "since", Type = ParameterType.DateTime, Placeholder = "{since}" });
    }

    [Test]
    public async Task Reject_ByItsRequester_WithdrawsTheChange_AndLeavesTheLiveVersionAlone()
    {
        var (store, active, proposal, request) = Pending(requestedBy: Ada);

        var decision = await Service(store).RejectAsync(request.Id, "withdrawn", CancellationToken.None);

        decision.Should().Be(new QueryApprovalDecision(request.Id, ApprovalStatus.Rejected, Ada, Ada));
        request.Status.Should().Be(ApprovalStatus.Rejected);
        proposal.Status.Should().Be(QueryVersionStatus.Rejected);
        active.Status.Should().Be(QueryVersionStatus.Active);
        store.Query.ActiveVersionId.Should().Be(active.Id);
        store.Offline.Commits.Should().Be(1);
    }

    [Test]
    public async Task ADecision_ByACallerWithoutAUserId_IsRefused()
    {
        var (store, active, proposal, request) = Pending(requestedBy: "ext-ben");
        var nobody = new Mock<IBeaconActorAccessor>();
        nobody.Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(BeaconActor.Nobody);

        var approve = () => Service(store, nobody.Object).ApproveAsync(request.Id, null, Shown(request), CancellationToken.None);
        var reject = () => Service(store, nobody.Object).RejectAsync(request.Id, null, CancellationToken.None);

        await approve.Should().ThrowAsync<BeaconForbiddenException>();
        await reject.Should().ThrowAsync<BeaconForbiddenException>();
        ShouldBeUntouched(store, active, proposal, request);
    }

    // The content hash the request's detail shows a reviewer: the proposal's stored hash, or its snapshot's.
    private static string? Shown(QueryApprovalRequest request) =>
        QueryContentHash.OfVersion(request.QueryVersion.ContentHash, request.QueryVersion.StepsJson, request.QueryVersion.FinalQuery);

    private static string Json(List<QueryStepSnapshot> steps) => JsonSerializer.Serialize(steps, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    // The query's active version 1, and a pending proposal 2 ("SELECT 2") made on it.
    private static (GovernanceStore Store, QueryVersion Active, QueryVersion Proposal, QueryApprovalRequest Request) Pending(string? requestedBy)
    {
        var store = new GovernanceStore();
        var active = store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var proposal = store.AddVersion(2, QueryVersionStatus.PendingApproval, "SELECT 2");
        var request = store.AddRequest(proposal, requestedBy, active.Id);

        return (store, active, proposal, request);
    }

    private static void ShouldBeUntouched(GovernanceStore store, QueryVersion active, QueryVersion proposal, QueryApprovalRequest request)
    {
        request.Status.Should().Be(ApprovalStatus.Pending);
        request.ReviewedByUserId.Should().BeNull();
        proposal.Status.Should().Be(QueryVersionStatus.PendingApproval);
        active.Status.Should().Be(QueryVersionStatus.Active);
        store.Query.ActiveVersionId.Should().Be(active.Id);
        store.Query.Steps.Should().ContainSingle().Which.SqlValue.Should().Be("SELECT 1");
        store.SaveCount.Should().Be(0);
        store.Offline.Commits.Should().Be(0);
    }

    // A signed-in, stored user ("ext-ada", Users.Id 3 unless given).
    private static IBeaconActorAccessor Reviewer(string externalId, int beaconUserId = 9, IReadOnlyList<string>? sessionIds = null)
    {
        var actor = new BeaconActor(externalId, false)
        {
            Kind = BeaconPrincipalKind.User,
            BeaconUserId = beaconUserId,
            IsActiveUser = true,
            SessionIds = sessionIds ?? []
        };
        var accessor = new Mock<IBeaconActorAccessor>();
        accessor
            .Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(actor);

        return accessor.Object;
    }

    private static QueryApprovalService Service(
        GovernanceStore store,
        IBeaconActorAccessor? reviewer = null,
        BeaconConfiguration? configuration = null,
        IStepSqlSaveCheck? saveCheck = null) =>
        TestQueryServices.ApprovalService(store.Factory(), reviewer ?? Reviewer(Ada, beaconUserId: 3), configuration, saveCheck);
}

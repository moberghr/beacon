using System.Text.Json;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.ChangeGovernance;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// With the approval workflow on, saving a change files a PendingApproval version holding the submitted content (not the
/// stored query, which approving would put back), checked like a direct save, with a request naming its requester (the
/// caller, by the id Beacon records people by) and the active version it was made on; the requester's own earlier pending
/// request is superseded, all in one transaction, and while another requester's request waits the submission is refused.
/// The live query is left alone until approved.
/// </summary>
[TestFixture]
public class QueryApprovalSubmissionTests
{
    [Test]
    public async Task UpdateQuery_ApprovalWorkflow_SubmitsTheEditedSqlAsTheCallerAndLeavesTheLiveQueryUntouched()
    {
        var store = new GovernanceStore();
        QueryData? proposed = null;
        QueryChangeSubmission? submission = null;
        var versions = new Mock<IQueryVersionService>();
        versions
            .Setup(x => x.SubmitForApprovalAsync(GovernanceStore.QueryId, It.IsAny<QueryData>(), It.IsAny<QueryChangeSubmission>(), It.IsAny<CancellationToken>()))
            .Callback<int, QueryData, QueryChangeSubmission, CancellationToken>((_, data, change, _) => (proposed, submission) = (data, change))
            .ReturnsAsync(new QueryVersionChange(new QueryVersion { Id = 50, QueryId = GovernanceStore.QueryId, Name = "Orders" }, 0));

        var response = await BuildService(store, versions.Object).UpdateQuery(GovernanceStore.Edit("SELECT 2"), CancellationToken.None);

        response.Message.Should().Be("Changes submitted for approval");
        proposed!.Steps.Should().ContainSingle().Which.SqlValue.Should().Be("SELECT 2");
        submission.Should().Be(new QueryChangeSubmission("ext-ada", "Ana Admin", "UserEdit", "Submitted for approval", "SQL query modified"));
        store.Query.Steps[0].SqlValue.Should().Be("SELECT 1", "the edit only goes live once approved");
        versions.Verify(
            x => x.CreateVersionAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<QueryVersionStatus>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task UpdateQuery_ApprovalWorkflow_BlockedKeyword_SubmitsNothing()
    {
        var store = new GovernanceStore();
        var versions = new Mock<IQueryVersionService>();

        var act = () => BuildService(store, versions.Object).UpdateQuery(GovernanceStore.Edit("DELETE FROM orders"), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconException>().WithMessage("*blocked SQL keywords*");
        versions.Verify(
            x => x.SubmitForApprovalAsync(It.IsAny<int>(), It.IsAny<QueryData>(), It.IsAny<QueryChangeSubmission>(), It.IsAny<CancellationToken>()),
            Times.Never);
        store.Saved.Should().BeEmpty();
    }

    [Test]
    public async Task SubmitForApprovalAsync_RecordsTheProposalWithItsHashRequesterAndBase_AndSupersedesTheRequestersEarlierPendingRequest()
    {
        var store = new GovernanceStore();
        store.User(3, "ext-ada", "Ada Lovelace");
        var active = store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var earlier = store.AddVersion(2, QueryVersionStatus.PendingApproval, "SELECT 9");
        var earlierRequest = store.AddRequest(earlier, "ext-ada", active.Id);
        var service = TestQueryServices.VersionService(store.Factory());

        await service.SubmitForApprovalAsync(
            GovernanceStore.QueryId,
            GovernanceStore.Edit("SELECT 2"),
            new QueryChangeSubmission("ext-ada", null, "UserEdit", "Submitted for approval", "SQL query modified"),
            CancellationToken.None);

        var version = store.Saved.OfType<QueryVersion>().Should().ContainSingle().Subject;
        version.Status.Should().Be(QueryVersionStatus.PendingApproval);
        version.VersionNumber.Should().Be(3);
        version.CreatedByUserId.Should().Be("ext-ada");
        version.CreatedByUserName.Should().Be("Ada Lovelace", "people are named by their label, never their user name");
        version.ContentHash.Should().MatchRegex("^[0-9a-f]{64}$").And.NotBe(active.ContentHash);
        version.DataSourceIds.Should().Equal([GovernanceStore.DataSourceId], "the version records the data sources its steps read");
        var steps = JsonSerializer.Deserialize<List<QueryStepSnapshot>>(version.StepsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        steps.Should().ContainSingle().Which.Should().BeEquivalentTo(new { SqlValue = "SELECT 2", DataSourceId = GovernanceStore.DataSourceId, DataSourceName = "warehouse" });
        store.Saved.OfType<QueryApprovalRequest>().Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            QueryId = GovernanceStore.QueryId,
            QueryVersion = version,
            Status = ApprovalStatus.Pending,
            RequestedByUserId = "ext-ada",
            RequestedByUserName = "Ada Lovelace",
            BaseVersionId = (int?)active.Id,
            ChangeSummary = "SQL query modified",
        }, x => x.ExcludingMissingMembers());
        earlierRequest.Status.Should().Be(ApprovalStatus.Superseded, "one request per query waits for a decision");
        earlier.Status.Should().Be(QueryVersionStatus.Superseded);
        active.Status.Should().Be(QueryVersionStatus.Active, "submitting changes nothing live");
        store.SaveCount.Should().Be(1);
        store.Offline.Commits.Should().Be(1, "the supersede and the new request commit together");
    }

    // Superseding is scoped to the query submitted for: another query's pending request and proposal stay pending.
    [Test]
    public async Task ASubmission_SupersedesOnlyItsOwnQuerysPendingRequests()
    {
        var store = new GovernanceStore();
        var active = store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var own = store.AddRequest(store.AddVersion(2, QueryVersionStatus.PendingApproval, "SELECT 9"), "ext-ada", active.Id);
        var other = new Query { Id = 8, Name = "Refunds" };
        var otherProposal = new QueryVersion { Id = 800, QueryId = 8, Query = other, VersionNumber = 1, Status = QueryVersionStatus.PendingApproval, Name = "Refunds", StepsJson = "[]" };
        var otherRequest = new QueryApprovalRequest { Id = 801, QueryId = 8, Query = other, QueryVersionId = 800, QueryVersion = otherProposal, Status = ApprovalStatus.Pending, RequestedByUserId = "ext-ben" };
        store.Queries.Add(other);
        store.Versions.Add(otherProposal);
        store.Requests.Add(otherRequest);

        var change = await TestQueryServices.VersionService(store.Factory())
            .SubmitForApprovalAsync(GovernanceStore.QueryId, GovernanceStore.Edit("SELECT 2"), Submission("ext-ada"), CancellationToken.None);

        change.SupersededRequests.Should().Be(1);
        own.Status.Should().Be(ApprovalStatus.Superseded);
        otherRequest.Status.Should().Be(ApprovalStatus.Pending, "another query's request is not this submission's to replace");
        otherProposal.Status.Should().Be(QueryVersionStatus.PendingApproval);
    }

    [Test]
    public async Task SubmitForApprovalAsync_UnknownDataSource_Throws()
    {
        var store = new GovernanceStore();
        var service = TestQueryServices.VersionService(store.Factory());
        var edit = GovernanceStore.Edit("SELECT 2");
        edit.Steps[0].DataSourceId = 42;

        var act = () => service.SubmitForApprovalAsync(GovernanceStore.QueryId, edit, new QueryChangeSubmission("ext-ada", null, "UserEdit", null, "SQL query modified"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Data source 42 not found.");
        store.Saved.Should().BeEmpty();
    }

    // Submissions are serialised on the query's row: each one supersedes what the previous one left pending, so however
    // many follow one another, exactly one request waits.
    [Test]
    public async Task TwoSubmissions_OneAfterTheOther_LeaveExactlyOnePendingRequest()
    {
        var store = new GovernanceStore();
        var active = store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var service = TestQueryServices.VersionService(store.Factory());

        await service.SubmitForApprovalAsync(GovernanceStore.QueryId, GovernanceStore.Edit("SELECT 2"), Submission("ext-ada"), CancellationToken.None);
        store.Commit();
        var first = store.Requests.Single();
        var second = await service.SubmitForApprovalAsync(GovernanceStore.QueryId, GovernanceStore.Edit("SELECT 3"), Submission("ext-ada"), CancellationToken.None);
        store.Commit();

        second.SupersededRequests.Should().Be(1);
        store.Requests.Where(x => x.Status == ApprovalStatus.Pending).Should().ContainSingle()
            .Which.QueryVersion.Should().BeSameAs(second.Version);
        first.Status.Should().Be(ApprovalStatus.Superseded);
        first.QueryVersion.Status.Should().Be(QueryVersionStatus.Superseded);
        second.Version.VersionNumber.Should().Be(3);
        store.Requests.Should().OnlyContain(x => x.BaseVersionId == active.Id);
        store.Offline.TransactionIsolationLevels.Should().HaveCount(2);
        store.Offline.Commits.Should().Be(2);
    }

    // Nobody could approve a request whose requester Beacon cannot name: with self-approval off it is refused before
    // anything is written (an API key without an active owner, a disabled user, an AI actor without a creator).
    [Test]
    public async Task ASubmissionWithoutARequester_IsRefusedUnlessTheHostAllowsSelfApproval()
    {
        var store = new GovernanceStore();
        store.Activate(store.AddVersion(1, QueryVersionStatus.Active));

        var refused = () => TestQueryServices.VersionService(store.Factory()).SubmitForApprovalAsync(GovernanceStore.QueryId, GovernanceStore.Edit("SELECT 2"), Submission(null), CancellationToken.None);

        await refused.Should().ThrowAsync<BeaconConflictException>().WithMessage(QueryVersionService.RequesterRequiredMessage);
        store.Saved.Should().BeEmpty();
        store.Offline.TransactionIsolationLevels.Should().BeEmpty();

        var selfApproval = new BeaconConfiguration { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true, AllowSelfApproval = true } };
        await TestQueryServices.VersionService(store.Factory(), configuration: selfApproval)
            .SubmitForApprovalAsync(GovernanceStore.QueryId, GovernanceStore.Edit("SELECT 2"), Submission(null), CancellationToken.None);

        store.Saved.OfType<QueryApprovalRequest>().Should().ContainSingle().Which.RequestedByUserId.Should().BeNull();
    }

    // Superseding is said: the response counts the requests replaced, and their requester is told (ApprovalUpdated
    // "superseded"), so the requester's other sessions refresh.
    [Test]
    public async Task ASubmission_TellsTheRequesterOfEachRequestItSupersedes()
    {
        var store = new GovernanceStore();
        var active = store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var earlier = store.AddRequest(store.AddVersion(2, QueryVersionStatus.PendingApproval, "SELECT 9"), "ext-ada", active.Id);
        var notifier = new Mock<IApprovalNotifier>();

        var change = await TestQueryServices.VersionService(store.Factory(), notifier: notifier.Object)
            .SubmitForApprovalAsync(GovernanceStore.QueryId, GovernanceStore.Edit("SELECT 2"), Submission("ext-ada"), CancellationToken.None);

        change.SupersededRequests.Should().Be(1);
        notifier.Verify(x => x.ApprovalUpdatedAsync(earlier.Id, "superseded", null, "ext-ada", It.IsAny<CancellationToken>()), Times.Once);
        notifier.VerifyNoOtherCalls();
    }

    // The submission is committed before its requesters are told: a push that fails is logged, not a failed submission.
    [Test]
    public async Task ASubmission_WhoseNotificationFails_StillStands()
    {
        var store = new GovernanceStore();
        var active = store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        store.AddRequest(store.AddVersion(2, QueryVersionStatus.PendingApproval, "SELECT 9"), "ext-ada", active.Id);
        var notifier = new Mock<IApprovalNotifier>();
        notifier
            .Setup(x => x.ApprovalUpdatedAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("hub down"));

        var change = await TestQueryServices.VersionService(store.Factory(), notifier: notifier.Object)
            .SubmitForApprovalAsync(GovernanceStore.QueryId, GovernanceStore.Edit("SELECT 2"), Submission("ext-ada"), CancellationToken.None);

        change.SupersededRequests.Should().Be(1);
        store.Offline.Commits.Should().Be(1);
    }

    // A submission replaces only its requester's own pending request: while another requester's waits (or one whose
    // requester Beacon could not name) it is refused, and the waiting request stays as it was.
    [TestCase("ext-ben", true)]
    [TestCase(null, true)]
    [TestCase("ext-ada", false)]
    [TestCase("EXT-ADA", false)]
    public async Task ASubmission_IsRefusedWhileAnotherRequestersRequestWaits_AndSupersedesOnlyItsRequestersOwn(string? pendingRequester, bool refused)
    {
        var store = new GovernanceStore();
        var active = store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var earlier = store.AddRequest(store.AddVersion(2, QueryVersionStatus.PendingApproval, "SELECT 9"), pendingRequester, active.Id);

        var act = () => TestQueryServices.VersionService(store.Factory())
            .SubmitForApprovalAsync(GovernanceStore.QueryId, GovernanceStore.Edit("SELECT 2"), Submission("ext-ada"), CancellationToken.None);

        if (refused)
        {
            await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(QueryVersionService.OtherPendingMessage);
            earlier.Status.Should().Be(ApprovalStatus.Pending);
            store.Saved.Should().BeEmpty();
            store.Offline.Commits.Should().Be(0);
            return;
        }

        (await act.Should().NotThrowAsync()).Subject.SupersededRequests.Should().Be(1);
        earlier.Status.Should().Be(ApprovalStatus.Superseded);
    }

    // A second editor's save never withdraws the first editor's pending change: it answers 409 and the first request still
    // waits for its decision.
    [Test]
    public async Task UpdateQuery_ByASecondEditor_WhileTheFirstEditorsChangeWaits_Is409AndTheFirstStaysPending()
    {
        var store = new GovernanceStore();
        var active = store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var first = store.AddVersion(2, QueryVersionStatus.PendingApproval, "SELECT 9");
        var firstRequest = store.AddRequest(first, "ext-ben", active.Id);

        var act = () => BuildService(store, TestQueryServices.VersionService(store.Factory(), configuration: WorkflowOn()))
            .UpdateQuery(GovernanceStore.Edit("SELECT 2"), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(QueryVersionService.OtherPendingMessage);
        firstRequest.Status.Should().Be(ApprovalStatus.Pending);
        first.Status.Should().Be(QueryVersionStatus.PendingApproval);
        store.Saved.Should().BeEmpty();
        store.Query.Steps.Should().ContainSingle().Which.SqlValue.Should().Be("SELECT 1");
    }

    // Once a reviewer rejected the first editor's change, the second editor's save is submitted, superseding nothing.
    [Test]
    public async Task UpdateQuery_ByASecondEditor_AfterTheFirstEditorsChangeWasRejected_IsSubmitted()
    {
        var store = new GovernanceStore();
        var active = store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var firstRequest = store.AddRequest(store.AddVersion(2, QueryVersionStatus.PendingApproval, "SELECT 9"), "ext-ben", active.Id);
        var service = BuildService(store, TestQueryServices.VersionService(store.Factory(), configuration: WorkflowOn()));

        var refused = () => service.UpdateQuery(GovernanceStore.Edit("SELECT 2"), CancellationToken.None);
        await refused.Should().ThrowAsync<BeaconConflictException>();

        await TestQueryServices.ApprovalService(store.Factory(), TestProjectAccess.Actors(9, "ext-cid"))
            .RejectAsync(firstRequest.Id, "not this one", CancellationToken.None);
        var response = await service.UpdateQuery(GovernanceStore.Edit("SELECT 2"), CancellationToken.None);

        response.Message.Should().Be("Changes submitted for approval");
        firstRequest.Status.Should().Be(ApprovalStatus.Rejected);
        store.Saved.OfType<QueryApprovalRequest>().Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Status = ApprovalStatus.Pending,
            RequestedByUserId = "ext-ada",
        }, x => x.ExcludingMissingMembers());
    }

    // The editor's base is compared again after the query's row is taken: a version that went live in between makes the
    // submission stale.
    [Test]
    public async Task ASubmissionMadeOnAVersionThatIsNoLongerActive_Is409()
    {
        var store = new GovernanceStore();
        var loaded = store.AddVersion(1, QueryVersionStatus.Archived);
        store.Activate(store.AddVersion(2, QueryVersionStatus.Active));

        var act = () => TestQueryServices.VersionService(store.Factory())
            .SubmitForApprovalAsync(GovernanceStore.QueryId, GovernanceStore.Edit("SELECT 2"), Submission("ext-ada") with { ExpectedBaseVersionId = loaded.Id }, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(QueryVersionService.StaleEditorMessage);
        store.Saved.Should().BeEmpty();
        store.Offline.Commits.Should().Be(0);
    }

    // History must hold the stored SQL an approval would replace: when no version holds it (changed outside a versioned
    // save, or never versioned), the submission records it first as an Archived baseline, in the same save.
    [TestCase(true)]
    [TestCase(false)]
    public async Task ASubmission_OverStoredSqlNoVersionHolds_RecordsItFirst(bool hasActiveVersion)
    {
        var store = new GovernanceStore();
        if (hasActiveVersion)
        {
            store.Activate(store.AddVersion(1, QueryVersionStatus.Active, "SELECT 0"));
        }

        var change = await TestQueryServices.VersionService(store.Factory())
            .SubmitForApprovalAsync(GovernanceStore.QueryId, GovernanceStore.Edit("SELECT 2"), Submission("ext-ada"), CancellationToken.None);

        var saved = store.Saved.OfType<QueryVersion>().ToList();
        saved.Should().HaveCount(2);
        saved[0].Should().BeEquivalentTo(new { Status = QueryVersionStatus.Archived, ChangeSource = QueryVersionService.BaselineSource, CreatedByUserId = (string?)null }, x => x.ExcludingMissingMembers());
        saved[0].ContentHash.Should().Be(QueryContentHash.Of(store.Query).Hash);
        saved[0].VersionNumber.Should().Be(change.Version.VersionNumber - 1);
        store.SaveCount.Should().Be(1, "the baseline and the proposal are one save");
    }

    [Test]
    public async Task ASubmission_OverStoredSqlTheActiveVersionHolds_RecordsNoBaseline()
    {
        var store = new GovernanceStore();
        store.Activate(store.AddVersion(1, QueryVersionStatus.Active));

        await TestQueryServices.VersionService(store.Factory())
            .SubmitForApprovalAsync(GovernanceStore.QueryId, GovernanceStore.Edit("SELECT 2"), Submission("ext-ada"), CancellationToken.None);

        store.Saved.OfType<QueryVersion>().Should().ContainSingle().Which.Status.Should().Be(QueryVersionStatus.PendingApproval);
    }

    [TestCase(1, 1)]
    [TestCase(1, 3)]
    [TestCase(0, 1)]
    public async Task ASubmissionWhoseStepOrdersDoNotRunInSequence_IsRefused(int firstOrder, int secondOrder)
    {
        var store = new GovernanceStore();
        var edit = GovernanceStore.Edit("SELECT 2");
        edit.Steps[0].StepOrder = firstOrder;
        edit.Steps.Add(new QueryStepData { StepOrder = secondOrder, Name = "Step 2", SqlValue = "SELECT 3", DataSourceId = GovernanceStore.DataSourceId });

        var act = () => TestQueryServices.VersionService(store.Factory()).SubmitForApprovalAsync(GovernanceStore.QueryId, edit, Submission("ext-ada"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(QueryStepOrders.Refusal);
        store.Saved.Should().BeEmpty();
    }

    // A new query's SQL waits for approval: the query is created with nothing live, its content is version 1 (made on no
    // version) with a pending request, all in one save and one transaction.
    [Test]
    public async Task SubmitNewQueryAsync_CreatesTheQueryWithNothingLive_AndItsContentPending()
    {
        var store = new GovernanceStore();
        var query = new Query { Name = "Late payments", OwnerUserId = "ext-ada" };
        var proposed = GovernanceStore.Edit("SELECT 2");
        proposed.FinalQuery = "SELECT * FROM @result1";

        var change = await TestQueryServices.VersionService(store.Factory())
            .SubmitNewQueryAsync(query, proposed, Submission("ext-ada"), CancellationToken.None);

        var created = store.Saved.OfType<Query>().Should().ContainSingle().Subject;
        created.Should().BeSameAs(query);
        created.Steps.Should().BeEmpty("nothing runs before approval");
        created.FinalQuery.Should().BeNull();
        created.Versions.Should().ContainSingle().Which.Should().BeSameAs(change.Version);
        change.Version.Should().BeEquivalentTo(new { VersionNumber = 1, Status = QueryVersionStatus.PendingApproval, FinalQuery = "SELECT * FROM @result1" }, x => x.ExcludingMissingMembers());
        store.Saved.OfType<QueryApprovalRequest>().Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Query = query,
            QueryVersion = change.Version,
            Status = ApprovalStatus.Pending,
            RequestedByUserId = "ext-ada",
            BaseVersionId = (int?)null,
        }, x => x.ExcludingMissingMembers());
        store.SaveCount.Should().Be(1);
        store.Offline.Commits.Should().Be(1);
    }

    [Test]
    public async Task SubmitNewQueryAsync_WithoutARequester_CreatesNothing()
    {
        var store = new GovernanceStore();

        var act = () => TestQueryServices.VersionService(store.Factory())
            .SubmitNewQueryAsync(new Query { Name = "Late payments" }, GovernanceStore.Edit("SELECT 2"), Submission(null), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(QueryVersionService.RequesterRequiredMessage);
        store.Saved.Should().BeEmpty();
    }

    // The new query's live steps would run unreviewed: a query carrying any is not submitted.
    [Test]
    public async Task SubmitNewQueryAsync_OfAQueryWithStepsOfItsOwn_IsRefused()
    {
        var store = new GovernanceStore();
        var query = new Query { Name = "Late payments", Steps = [new QueryStep { QueryId = 0, DataSourceId = GovernanceStore.DataSourceId, StepOrder = 1, SqlValue = "SELECT 1" }] };

        var act = () => TestQueryServices.VersionService(store.Factory())
            .SubmitNewQueryAsync(query, GovernanceStore.Edit("SELECT 2"), Submission("ext-ada"), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        store.Saved.Should().BeEmpty();
    }

    // With the workflow on, a query created with steps (IQueryService.CreateQuery) has nothing live until approved.
    [Test]
    public async Task CreateQuery_WithTheWorkflowOn_SubmitsItsSqlAndStoresNothingLive()
    {
        var store = new GovernanceStore();
        var versions = new Mock<IQueryVersionService>();
        versions
            .Setup(x => x.SubmitNewQueryAsync(It.IsAny<Query>(), It.IsAny<QueryData>(), It.IsAny<QueryChangeSubmission>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryVersionChange(new QueryVersion { Id = 50, QueryId = 88, Name = "Late payments" }, 0));
        var service = BuildService(store, versions.Object);
        var data = GovernanceStore.Edit("SELECT 2");
        data.Name = "Late payments";

        var response = await service.CreateQuery(data, CancellationToken.None);

        response.Message.Should().Be(QueryService.NewQuerySubmittedMessage);
        versions.Verify(
            x => x.SubmitNewQueryAsync(
                It.Is<Query>(y => y.Name == "Late payments" && y.OwnerUserId == "ext-ada" && y.Steps.Count == 0),
                It.Is<QueryData>(y => y.Steps.Single().SqlValue == "SELECT 2"),
                It.Is<QueryChangeSubmission>(y => y.RequesterUserId == "ext-ada"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        store.Saved.OfType<Query>().Should().BeEmpty("nothing is stored live until approved");
    }

    [Test]
    public void RecordedDataSourceIds_AreTheDistinctSourcesInOrder()
    {
        QueryVersionService.RecordedDataSourceIds([7, 3, 7, 5]).Should().Equal(3, 5, 7);
        QueryVersionService.RecordedDataSourceIds([]).Should().BeEmpty();
    }

    private static QueryChangeSubmission Submission(string? requester) => new(requester, null, "UserEdit", "Submitted for approval", "SQL query modified");

    private static BeaconConfiguration WorkflowOn() => new() { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true } };

    private static QueryService BuildService(GovernanceStore store, IQueryVersionService versions) =>
        TestQueryServices.QueryService(
            store.Factory(),
            versions: versions,
            configuration: WorkflowOn(),
            userContext: Mock.Of<IBeaconUserContext>(x => x.UserId == "u1" && x.DisplayName == "Ana Admin"));
}

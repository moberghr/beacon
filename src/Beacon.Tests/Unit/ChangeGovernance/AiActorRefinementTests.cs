using System.Text.Json;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.Ai.AiActor.Models;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Ai;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using ActorEntity = Beacon.Core.Data.Entities.AiActor;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// An AI actor's refinement is validated as a draft (the stored query never runs SQL nobody saved), then, with the
/// approval workflow on, submitted for approval with the actor's creator as its requester — recording the stored SQL it
/// would replace, never replacing someone else's pending change, and failing for an actor without a creator; without the
/// workflow it is a versioned save attributed to the creator. A new query is validated as a draft before anything is
/// stored, and with the workflow on it is created with nothing live and its SQL submitted. With the workflow on an actor
/// subscribes only to a query with an approved active version. Approving a plan is a conditional decision from
/// PendingApproval, and the query changes it submits record the plan's reviewer and revision requesters.
/// </summary>
[TestFixture]
public class AiActorRefinementTests
{
    private const int ActorId = 5;
    private const string Creator = "ext-ana";

    private Mock<IQueryService> _queries = null!;
    private Mock<IQueryVersionService> _versions = null!;
    private QueryDraft? _validated;

    [SetUp]
    public void SetUp()
    {
        _queries = new Mock<IQueryService>(MockBehavior.Strict);
        _versions = new Mock<IQueryVersionService>();
        _validated = null;
    }

    [Test]
    public async Task ARefinement_WithTheWorkflowOn_IsValidatedAsADraftAndSubmittedAsTheCreators()
    {
        var store = OwnedQuery();
        DraftRuns(true);

        var action = await Service(store, WorkflowOn()).ExecuteOrProposeAsync(Actor(), Refine("SELECT 2"), [], CancellationToken.None);

        action.Success.Should().BeTrue();
        _validated!.Steps.Should().ContainSingle().Which.SqlValue.Should().Be("SELECT 2");
        _versions.Verify(
            x => x.SubmitForApprovalAsync(
                GovernanceStore.QueryId,
                It.Is<QueryData>(y => y.Steps.Single().SqlValue == "SELECT 2" && y.Steps.Single().StepId == GovernanceStore.StepId),
                It.Is<QueryChangeSubmission>(y => y.RequesterUserId == Creator
                    && y.Source == AiActorService.AiActorChangeSource
                    && y.Reason == "narrower"
                    && y.Summary == "AI actor refinement"
                    && !y.SupersedesOthers
                    && y.ContributorUserIds.Count == 0),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "a refinement never replaces another person's pending change of the query; outside a plan nobody else shaped it");
        _versions.VerifyNoOtherCalls();
        store.Query.Steps[0].SqlValue.Should().Be("SELECT 1", "the refinement goes live only once approved");
        store.Saved.Should().BeEmpty();
    }

    // Through the real submission: the stored SQL the refinement would replace is recorded first when no version holds it,
    // in the submission's own transaction.
    [Test]
    public async Task ARefinement_WithTheWorkflowOn_RecordsTheStoredSqlItWouldReplace()
    {
        var store = OwnedQuery();
        store.Activate(store.AddVersion(1, QueryVersionStatus.Active, "SELECT 0"));
        DraftRuns(true);

        var action = await Service(store, WorkflowOn(), TestQueryServices.VersionService(store.Factory(), configuration: WorkflowOn()))
            .ExecuteOrProposeAsync(Actor(), Refine("SELECT 2"), [], CancellationToken.None);

        action.Success.Should().BeTrue();
        var saved = store.Saved.OfType<QueryVersion>().ToList();
        saved.Should().HaveCount(2);
        saved[0].Should().BeEquivalentTo(new { Status = QueryVersionStatus.Archived, ChangeSource = QueryVersionService.BaselineSource, VersionNumber = 2 }, x => x.ExcludingMissingMembers());
        saved[0].StepsJson.Should().Contain("SELECT 1", "the stored SQL, which no version held");
        saved[1].Should().BeEquivalentTo(new { Status = QueryVersionStatus.PendingApproval, VersionNumber = 3 }, x => x.ExcludingMissingMembers());
        store.Offline.Commits.Should().Be(1, "the baseline and the proposal commit together");
    }

    // A refinement never replaces someone else's pending change of the query: the action fails, the request stays.
    [Test]
    public async Task ARefinement_WhileSomeoneElsesChangeIsPending_FailsAndSupersedesNothing()
    {
        var store = OwnedQuery();
        var active = store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var pending = store.AddRequest(store.AddVersion(2, QueryVersionStatus.PendingApproval, "SELECT 9"), "ext-ben", active.Id);
        DraftRuns(true);

        var action = await Service(store, WorkflowOn(), TestQueryServices.VersionService(store.Factory(), configuration: WorkflowOn()))
            .ExecuteOrProposeAsync(Actor(), Refine("SELECT 2"), [], CancellationToken.None);

        action.Success.Should().BeFalse();
        action.ErrorMessage.Should().Be(QueryVersionService.OtherPendingMessage);
        pending.Status.Should().Be(ApprovalStatus.Pending);
        store.Saved.Should().BeEmpty();
    }

    // An actor without a creator has nobody to request its change, so nobody could approve it: the action fails instead of
    // reporting a submission.
    [Test]
    public async Task ARefinement_OfAnActorWithoutACreator_Fails()
    {
        var store = OwnedQuery();
        store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        DraftRuns(true);
        var ownerless = Actor();
        ownerless.CreatedByUserId = null;

        var action = await Service(store, WorkflowOn(), TestQueryServices.VersionService(store.Factory(), configuration: WorkflowOn()))
            .ExecuteOrProposeAsync(ownerless, Refine("SELECT 2"), [], CancellationToken.None);

        action.Success.Should().BeFalse();
        action.ErrorMessage.Should().Be(QueryVersionService.RequesterRequiredMessage);
        store.Saved.Should().BeEmpty();
    }

    [Test]
    public async Task ARefinement_WithTheWorkflowOff_IsAVersionedSaveAttributedToTheCreator()
    {
        var store = OwnedQuery();
        DraftRuns(true);

        var action = await Service(store, new BeaconConfiguration()).ExecuteOrProposeAsync(Actor(), Refine("SELECT 2"), [], CancellationToken.None);

        action.Success.Should().BeTrue();
        store.Query.Steps[0].SqlValue.Should().Be("SELECT 2");
        store.Saved.OfType<QueryStepChangeHistory>().Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            PreviousSql = "SELECT 1",
            NewSql = "SELECT 2",
            AiActorId = ActorId,
            DataSourceId = GovernanceStore.DataSourceId,
        }, x => x.ExcludingMissingMembers());
        _versions.Verify(x => x.EnsureBaselineVersionAsync(GovernanceStore.QueryId, It.IsAny<CancellationToken>()), Times.Once);
        _versions.Verify(x => x.CreateVersionAsync(GovernanceStore.QueryId, Creator, AiActorService.AiActorChangeSource, "narrower", QueryVersionStatus.Active, It.IsAny<CancellationToken>()), Times.Once);
        _versions.VerifyNoOtherCalls();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ARefinementWhoseDraftFails_ChangesNothing(bool workflowOn)
    {
        var store = OwnedQuery();
        DraftRuns(false);

        var action = await Service(store, workflowOn ? WorkflowOn() : new BeaconConfiguration()).ExecuteOrProposeAsync(Actor(), Refine("SELECT 2"), [], CancellationToken.None);

        action.Success.Should().BeFalse();
        action.ErrorMessage.Should().StartWith("Refined query validation failed");
        store.Query.Steps[0].SqlValue.Should().Be("SELECT 1");
        store.Saved.Should().BeEmpty();
        _versions.VerifyNoOtherCalls();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ANewQuery_IsValidatedAsADraftBeforeAnythingIsStored(bool runs)
    {
        var store = new GovernanceStore();
        DraftRuns(runs);

        var action = await Service(store, new BeaconConfiguration()).ExecuteOrProposeAsync(Actor(), Create("SELECT 3"), [], CancellationToken.None);

        action.Success.Should().Be(runs);
        _validated!.Steps.Should().ContainSingle().Which.Should().BeEquivalentTo(new { SqlValue = "SELECT 3", DataSourceId = GovernanceStore.DataSourceId }, x => x.ExcludingMissingMembers());
        store.Saved.OfType<Query>().Should().HaveCount(runs ? 1 : 0, "a query whose SQL does not run is never stored");
    }

    // With the workflow on, the new query's SQL waits for approval: created with nothing live and submitted as the
    // creator's first version, so no schedule runs it before a second person approves it.
    [Test]
    public async Task ANewQuery_WithTheWorkflowOn_IsCreatedWithNothingLiveAndItsSqlSubmitted()
    {
        var store = new GovernanceStore();
        DraftRuns(true);
        _versions
            .Setup(x => x.SubmitNewQueryAsync(It.IsAny<Query>(), It.IsAny<QueryData>(), It.IsAny<QueryChangeSubmission>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryVersionChange(new QueryVersion { Id = 500, QueryId = 88, Name = "late payments", StepsJson = "[]", Status = QueryVersionStatus.PendingApproval }, 0));

        var action = await Service(store, WorkflowOn()).ExecuteOrProposeAsync(Actor(), Create("SELECT 3"), [], CancellationToken.None);

        action.Success.Should().BeTrue();
        action.ResultEntityId.Should().Be(88);
        _versions.Verify(
            x => x.SubmitNewQueryAsync(
                It.Is<Query>(y => y.AiActorId == ActorId && y.OwnerUserId == Creator && y.Steps.Count == 0 && y.FinalQuery == null),
                It.Is<QueryData>(y => y.Steps.Single().SqlValue == "SELECT 3" && y.Steps.Single().DataSourceId == GovernanceStore.DataSourceId),
                It.Is<QueryChangeSubmission>(y => y.RequesterUserId == Creator && y.Source == AiActorService.AiActorChangeSource),
                It.IsAny<CancellationToken>()),
            Times.Once);
        store.Saved.OfType<Query>().Should().BeEmpty("nothing is stored live");
    }

    // With the workflow on, an actor schedules only SQL someone approved: its query's active version, approved for exactly
    // its content.
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task ASubscription_WithTheWorkflowOn_NeedsAnApprovedActiveVersion(bool hasActiveVersion, bool approvedForItsHash)
    {
        var store = OwnedQuery();
        if (hasActiveVersion)
        {
            var active = store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
            store.AddRequest(active, "ext-ana", null, ApprovalStatus.Approved, approvedContentHash: approvedForItsHash ? active.ContentHash : null, reviewedBy: "ext-ben");
        }

        var action = await Service(store, WorkflowOn()).ExecuteOrProposeAsync(Actor(), Subscribe(), [], CancellationToken.None);

        action.Success.Should().Be(approvedForItsHash);
        store.Saved.OfType<Subscription>().Should().HaveCount(approvedForItsHash ? 1 : 0);
        if (!approvedForItsHash)
        {
            action.ErrorMessage.Should().EndWith(AiActorService.NotApprovedForSubscriptionMessage);
        }
    }

    [Test]
    public async Task ASubscription_WithTheWorkflowOff_IsCreatedAsBefore()
    {
        var store = OwnedQuery();

        var action = await Service(store, new BeaconConfiguration()).ExecuteOrProposeAsync(Actor(), Subscribe(), [], CancellationToken.None);

        action.Success.Should().BeTrue();
        store.Saved.OfType<Subscription>().Should().ContainSingle();
    }

    // Approving a plan executes its actions as the actor's creator's changes; the plan's reviewer and whoever asked for its
    // revisions shaped them, and are recorded on the query changes it submits so none of them approves those.
    [Test]
    public async Task ApprovingAPlan_RecordsItsReviewerAndRevisionRequestersOnTheChangesItSubmits()
    {
        var store = OwnedQuery();
        var actor = Actor();
        actor.DataSource = store.Warehouse;
        store.Plans.Add(new AiActorPlan { Id = 30, AiActorId = ActorId, AiActor = actor, Status = AiActorPlanStatus.RevisionRequested, ReviewedByUserId = "ext-cid", Analysis = "a", ActionsJson = "[]" });
        store.Plans.Add(new AiActorPlan { Id = 31, AiActorId = ActorId, AiActor = actor, ParentPlanId = 30, Status = AiActorPlanStatus.PendingApproval, Analysis = "a", ActionsJson = RefinementActions("SELECT 2") });
        DraftRuns(true);

        var result = await Service(store, WorkflowOn()).ApprovePlanAsync(new ApprovePlanOptions { PlanId = 31, UserId = "ext-ben" }, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.QueriesRefined.Should().Be(1);
        store.Plans.Single(x => x.Id == 31).Should().BeEquivalentTo(new { Status = AiActorPlanStatus.Executed, ReviewedByUserId = "ext-ben" }, x => x.ExcludingMissingMembers());
        _versions.Verify(
            x => x.SubmitForApprovalAsync(
                GovernanceStore.QueryId,
                It.IsAny<QueryData>(),
                It.Is<QueryChangeSubmission>(y => y.RequesterUserId == Creator && y.ContributorUserIds.SequenceEqual(new[] { "ext-ben", "ext-cid" })),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestCase(AiActorPlanStatus.Executing)]
    [TestCase(AiActorPlanStatus.Rejected)]
    [TestCase(AiActorPlanStatus.RevisionRequested)]
    public async Task ApprovingAPlanThatIsNoLongerPending_Is409AndRunsNothing(AiActorPlanStatus status)
    {
        var store = OwnedQuery();
        var actor = Actor();
        actor.DataSource = store.Warehouse;
        store.Plans.Add(new AiActorPlan { Id = 31, AiActorId = ActorId, AiActor = actor, Status = status, ReviewedByUserId = "ext-cid", Analysis = "a", ActionsJson = RefinementActions("SELECT 2") });

        var act = () => Service(store, WorkflowOn()).ApprovePlanAsync(new ApprovePlanOptions { PlanId = 31, UserId = "ext-ben" }, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(AiActorService.PlanNotPendingMessage);
        store.Plans.Single().Should().BeEquivalentTo(new { Status = status, ReviewedByUserId = "ext-cid" }, x => x.ExcludingMissingMembers());
        _versions.VerifyNoOtherCalls();
    }

    // Asking for a revision records who asked, on the plan the revision's chain names.
    [Test]
    public async Task RequestingARevision_OfAPlanThatIsNoLongerPending_Is409()
    {
        var store = OwnedQuery();
        store.Plans.Add(new AiActorPlan { Id = 31, AiActorId = ActorId, Status = AiActorPlanStatus.Rejected, Analysis = "a", ActionsJson = "[]" });

        var act = () => Service(store, WorkflowOn()).RequestPlanRevisionAsync(new RequestRevisionOptions { PlanId = 31, UserId = "ext-ben", Feedback = "narrower" }, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(AiActorService.PlanNotPendingMessage);
        store.Plans.Single().Status.Should().Be(AiActorPlanStatus.Rejected);
    }

    private void DraftRuns(bool success)
    {
        _queries
            .Setup(x => x.PreviewQuery(It.IsAny<int>(), It.IsAny<QueryDraft>(), It.IsAny<ListRequest>(), It.IsAny<CancellationToken>()))
            .Callback<int, QueryDraft?, ListRequest, CancellationToken>((_, draft, _, _) => _validated = draft)
            .ReturnsAsync(new QueryPreviewResult { Success = success, ErrorMessage = success ? null : "relation does not exist" });
    }

    private static GovernanceStore OwnedQuery()
    {
        var store = new GovernanceStore();
        store.Query.AiActorId = ActorId;

        return store;
    }

    private static ActorEntity Actor() =>
        new()
        {
            Id = ActorId,
            Name = "watcher",
            Instructions = "watch",
            DataSourceId = GovernanceStore.DataSourceId,
            RequiresApproval = false,
            MaxQueries = 10,
            MaxSubscriptionsPerQuery = 10,
            CreatedByUserId = Creator
        };

    private static AiActorActionPlan Refine(string sql) =>
        new()
        {
            ActionType = "REFINE_QUERY",
            Reasoning = "r",
            Parameters = new Dictionary<string, object?> { ["queryId"] = GovernanceStore.QueryId, ["newSql"] = sql, ["reason"] = "narrower" }
        };

    private static AiActorActionPlan Create(string sql) =>
        new()
        {
            ActionType = "CREATE_QUERY",
            Reasoning = "r",
            Parameters = new Dictionary<string, object?> { ["name"] = "late payments", ["sql"] = sql }
        };

    private static AiActorActionPlan Subscribe() =>
        new()
        {
            ActionType = "CREATE_SUBSCRIPTION",
            Reasoning = "r",
            Parameters = new Dictionary<string, object?> { ["queryId"] = GovernanceStore.QueryId, ["cronExpression"] = "0 * * * *" }
        };

    // A plan's actions as GeneratePlanAsync stores them: one refinement of the store's query.
    private static string RefinementActions(string sql) =>
        JsonSerializer.Serialize(
            new List<ProposedAction>
            {
                new()
                {
                    ActionType = AiActorActionType.RefineQuery,
                    Reasoning = "r",
                    Parameters = new Dictionary<string, object?> { ["queryId"] = GovernanceStore.QueryId, ["newSql"] = sql, ["reason"] = "narrower" },
                    TargetQueryId = GovernanceStore.QueryId
                }
            },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    private static BeaconConfiguration WorkflowOn() => new() { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true } };

    private AiActorService Service(GovernanceStore store, BeaconConfiguration configuration, IQueryVersionService? versions = null) =>
        new(
            store.Factory(),
            Mock.Of<ILlmProvider>(MockBehavior.Strict),
            Mock.Of<IDatabaseMetadataService>(MockBehavior.Strict),
            _queries.Object,
            Mock.Of<ISubscriptionService>(MockBehavior.Strict),
            Mock.Of<IBeaconScheduler>(MockBehavior.Strict),
            Mock.Of<IRunAsScope>(),
            TestProjectAccess.ExecutionAllowed(),
            Mock.Of<IStepSqlSaveCheck>(),
            versions ?? _versions.Object,
            configuration,
            NullLogger<AiActorService>.Instance);
}

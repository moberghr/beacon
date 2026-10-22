using Beacon.Core.Models;
using Beacon.Core.Authorization;
using System.Data;
using System.Text.Json;
using Beacon.AI.Handlers.AiActors;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.Ai.AiActor.Models;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;
using Beacon.Core.Handlers.AiActors;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Models.Queries;
using Beacon.Core.Helpers;
using Beacon.Core.Services;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Moq;
using NUnit.Framework;
using Npgsql;
using ActorEntity = Beacon.Core.Data.Entities.AiActor;
using Beacon.Core.SecurityAudit;

namespace Beacon.Tests.Unit;

/// <summary>
/// The three cycle loops — think, refine and initial setup — must route every LLM-planned action through the
/// propose-only seam: an actor that requires approval stores its actions in <c>ActionsJson</c> as proposals and
/// neither archives nor inserts anything. Driven end to end with a stubbed LLM over a mocked context (async-queryable
/// doubles, no database — §4.7); <see cref="IQueryService"/> / <see cref="ISubscriptionService"/> are strict, so any
/// executed archive fails the test, and the query / subscription sets record any insert.
/// </summary>
[TestFixture]
public class AiActorCycleProposeOnlyTests
{
    private const int ActorId = 5;
    private const int DataSourceId = 1;
    private const int OwnedQueryId = 1;

    // The actor's own unlocked query is the archive target, so an executed (not proposed) archive would reach
    // IQueryService.DeleteQuery rather than stop at the ownership guard.
    private const string PlanJson = """
        {
          "analysis": "one stale monitor, one gap",
          "findings": [],
          "actions": [
            { "actionType": "ARCHIVE_QUERY", "reasoning": "stale", "parameters": { "queryId": 1 } },
            { "actionType": "CREATE_QUERY", "reasoning": "gap", "parameters": { "name": "Failed payments", "sql": "SELECT 1" } }
          ],
          "shouldNotify": false
        }
        """;

    private const string MalformedPlanJson = """
        {
          "analysis": "malformed actions",
          "findings": [],
          "actions": [
            { "actionType": null, "reasoning": "no type", "parameters": { "queryId": 1 } },
            { "actionType": "ARCHIVE_QUERY", "reasoning": "no parameters", "parameters": null },
            null,
            { "actionType": "CREATE_QUERY", "reasoning": "gap", "parameters": { "name": "Failed payments", "sql": "SELECT 1" } }
          ],
          "shouldNotify": false
        }
        """;

    // Two archives: the actor's own query, and a query of another actor that the executor's ownership guard refuses.
    private const string ArchivesJson = """
        {
          "analysis": "two stale monitors",
          "findings": ["both return nothing"],
          "actions": [
            { "actionType": "ARCHIVE_QUERY", "reasoning": "stale", "parameters": { "queryId": 1 } },
            { "actionType": "ARCHIVE_QUERY", "reasoning": "also stale", "parameters": { "queryId": 2 } }
          ],
          "shouldNotify": false
        }
        """;

    private const string NoActionsJson = """
        { "analysis": "all quiet", "findings": [], "actions": [], "shouldNotify": false }
        """;

    private const int OtherActorsQueryId = 2;

    private const int LockedQueryId = 3;

    private const string CreateOnlyJson = """
        {
          "analysis": "one gap",
          "findings": [],
          "actions": [
            { "actionType": "CREATE_QUERY", "reasoning": "gap", "parameters": { "name": "Failed payments", "sql": "SELECT 1" } }
          ],
          "shouldNotify": false
        }
        """;

    // Two refinements: the actor's own unlocked query, and its own locked one.
    private const string RefinementsJson = """
        {
          "analysis": "narrower monitors",
          "findings": [],
          "actions": [
            { "actionType": "REFINE_QUERY", "reasoning": "narrower", "parameters": { "queryId": 1, "newSql": "SELECT 2" } },
            { "actionType": "REFINE_QUERY", "reasoning": "narrower too", "parameters": { "queryId": 3, "newSql": "SELECT 4" } }
          ],
          "shouldNotify": false
        }
        """;

    private const string LockedRefinementJson = """
        {
          "analysis": "narrower monitor",
          "findings": [],
          "actions": [
            { "actionType": "REFINE_QUERY", "reasoning": "narrower", "parameters": { "queryId": 3, "newSql": "SELECT 4" } }
          ],
          "shouldNotify": false
        }
        """;

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);

    private Store _store = null!;
    private Mock<DbSet<Query>> _queriesSet = null!;
    private Mock<DbSet<Subscription>> _subscriptionsSet = null!;
    private Mock<IQueryService> _queryService = null!;
    private Mock<ISubscriptionService> _subscriptionService = null!;
    private RecordingSecurityAudit _audit = null!;
    private Mock<ILogger<AiActorService>> _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = new Mock<ILogger<AiActorService>>();
        _audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(12, "ext-ben"));
        _store = new Store();
        _store.DataSources.Add(
            new DataSource
            {
                Id = DataSourceId,
                Name = "payments",
                DataSourceType = DataSourceType.Database,
                EncryptedConnectionData = "not-a-connection-string"
            });
        _store.Queries.Add(
            new Query
            {
                Id = OwnedQueryId,
                Name = "Stale monitor",
                AiActorId = ActorId
            });

        _queriesSet = BuildSet(_store.Queries);
        _subscriptionsSet = BuildSet(_store.Subscriptions);
        _queryService = new Mock<IQueryService>(MockBehavior.Strict);
        _subscriptionService = new Mock<ISubscriptionService>(MockBehavior.Strict);
    }

    // A generated plan waits for a reviewer: its submission is on record (its actor, version and how many actions it
    // proposes), never its analysis or the SQL it proposes.
    [Test]
    public async Task GeneratePlanAsync_RecordsThePlansSubmission()
    {
        SeedActor();
        var service = BuildService(PlanJson);

        var result = await service.GeneratePlanAsync(new GeneratePlanOptions { ActorId = ActorId }, CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        var plan = _store.Plans.Should().ContainSingle().Subject;
        var submitted = _audit.Single("one stale monitor", "Failed payments", "SELECT 1");
        submitted.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.AiPlanSubmitted, ActorUserId = (int?)12, ResourceType = SecurityAuditResources.AiActorPlan, ResourceId = plan.Id.ToString() },
            x => x.ExcludingMissingMembers());
        submitted.DetailsJson.Should().Be("{\"aiActorId\":5,\"parentPlanId\":null,\"version\":1,\"proposedActionCount\":2}");
        _audit.Ways.Should().Equal("saved");
        _store.Offline.TransactionIsolationLevels.Should().Equal(IsolationLevel.Serializable);
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_RequiresApproval_StoresProposalsAsOnePendingPlanAndExecutesNothing()
    {
        var actor = SeedActor();
        var service = BuildService(PlanJson);

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        result.Success.Should().BeTrue();
        actor.Status.Should().Be(AiActorStatus.Active);
        var execution = SingleCompletedExecution();
        AssertProposedAndNothingExecuted(execution);
        result.PlanId.Should().Be(AssertOnePendingPlan(execution).Id);
        _store.Offline.TransactionIsolationLevels.Should().Equal(IsolationLevel.Serializable);
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_NoApprovalRequired_RunsTheActionsAndProposesNoPlan()
    {
        var actor = SeedActor();
        actor.RequiresApproval = false;
        _queryService.Setup(x => x.DeleteQuery(OwnedQueryId, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var service = BuildService(ArchivesJson);

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.PlanId.Should().BeNull();
        _store.Plans.Should().BeEmpty();
        _queryService.Verify(x => x.DeleteQuery(OwnedQueryId, It.IsAny<CancellationToken>()), Times.Once);
        _audit.Of(SecurityAuditActions.AiPlanSubmitted).Should().BeEmpty();
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_RequiresApproval_NoActions_ProposesNoPlan()
    {
        SeedActor();
        var service = BuildService(NoActionsJson);

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.PlanId.Should().BeNull();
        _store.Plans.Should().BeEmpty();
        SingleCompletedExecution().DetailedAnalysis.Should().BeNull();
    }

    // A plan already waiting for a reviewer stays as it is: a later cycle is skipped before it reaches the model.
    [Test]
    public async Task ExecuteThinkCycleAsync_WhileAPlanIsPending_IsSkippedBeforeTheLlmCall()
    {
        var actor = SeedActor();
        var pending = SeedPlan(actor, 40, Now.AddDays(-6));
        var llmCalls = 0;
        var service = BuildService(PlanJson, () => llmCalls++, time: new FixedTimeProvider(Now), cooldownMinutes: 0);

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        llmCalls.Should().Be(0);
        result.Success.Should().BeFalse();
        result.Phase.Should().Be(AiActorExecutionPhase.Skipped);
        result.PlanId.Should().BeNull();
        result.ErrorMessage.Should().Contain(AiActorService.PlanPendingMessage).And.Contain("40");
        var execution = _store.Executions.Should().ContainSingle().Subject;
        execution.Phase.Should().Be(AiActorExecutionPhase.Skipped);
        execution.ErrorMessage.Should().Be(result.ErrorMessage);
        _store.Plans.Should().ContainSingle().Which.Should().BeSameAs(pending);
        pending.Status.Should().Be(AiActorPlanStatus.PendingApproval);
        actor.Status.Should().Be(AiActorStatus.Active);
        AssertNothingExecuted();
        _audit.Events.Should().BeEmpty();
    }

    // Another plan became pending while the cycle ran (a cycle that finished first): no second plan is added, and the
    // result says the proposals were not submitted.
    [Test]
    public async Task ExecuteThinkCycleAsync_WhenAPlanBecamePendingDuringTheCycle_SubmitsNoSecondPlan()
    {
        var actor = SeedActor();
        var service = BuildService(PlanJson, () => SeedPlan(actor, 40, DateTime.UtcNow));

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.PlanId.Should().BeNull();
        result.ProposalsNotSubmittedReason.Should().Be(AiActorService.ProposalsNotSubmittedMessage);
        _store.Plans.Should().ContainSingle().Which.Id.Should().Be(40);
        _store.Offline.TransactionIsolationLevels.Should().Equal(IsolationLevel.Serializable);
        var execution = SingleCompletedExecution();
        execution.DetailedAnalysis.Should().Contain("Plan 40");
        actor.ThinkCount.Should().Be(1, "the cycle's completion is saved although its plan was refused");
        AssertProposedAndNothingExecuted(execution);
        _audit.Of(SecurityAuditActions.AiPlanSubmitted).Should().BeEmpty();
    }

    // A plan became pending while the refinement ran: the refinement is on record, its plan is refused with a 409.
    [Test]
    public async Task RefineActorAsync_WhenAPlanBecamePendingDuringTheRefinement_Is409AndAddsNoPlan()
    {
        var actor = SeedActor();
        var service = BuildService(PlanJson, () => SeedPlan(actor, 40, DateTime.UtcNow));

        var act = () => service.RefineActorAsync(ActorId, "also watch failed payments", CancellationToken.None);

        (await act.Should().ThrowAsync<BeaconConflictException>()).Which.Message
            .Should().Be($"{AiActorService.PlanPendingMessage} Plan 40.");
        _store.Plans.Should().ContainSingle().Which.Id.Should().Be(40);
        _store.Offline.TransactionIsolationLevels.Should().Equal(IsolationLevel.Serializable);
        SingleCompletedExecution().DetailedAnalysis.Should().Contain("Plan 40");
        _audit.Of(SecurityAuditActions.AiPlanSubmitted).Should().BeEmpty();
    }

    // A concurrent admission wins the serializable transaction: the cycle completes, its plan and event are not written.
    [Test]
    public async Task ExecuteThinkCycleAsync_WhenThePlansAdmissionLosesASerializationConflict_SubmitsNoPlan()
    {
        var actor = SeedActor();
        var service = BuildService(PlanJson, audit: new FailingSubmissionAudit(_store, SerializationFailure()));

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.PlanId.Should().BeNull();
        result.ProposalsNotSubmittedReason.Should().Be(AiActorService.ProposalsNotSubmittedMessage);
        actor.Status.Should().Be(AiActorStatus.Active);
        var execution = SingleCompletedExecution();
        execution.DetailedAnalysis.Should().Be(AiActorService.ProposalsNotSubmittedMessage);
        _store.TrackedTypesAtSave.Last().Should().NotContain(typeof(AiActorPlan)).And.NotContain(typeof(SecurityAuditEvent));
    }

    [Test]
    public async Task RequestPlanRevisionAsync_WhenTheRevisionLosesASerializationConflict_Is409()
    {
        var actor = SeedActor();
        var plan = SeedPlan(actor, 40, DateTime.UtcNow.AddDays(-1));
        var service = BuildService(PlanJson, audit: new FailingSubmissionAudit(_store, SerializationFailure()));

        var act = () => service.RequestPlanRevisionAsync(new RequestRevisionOptions { PlanId = 40, UserId = "ext-ben", Feedback = "narrower" }, CancellationToken.None);

        (await act.Should().ThrowAsync<BeaconConflictException>()).Which.Message
            .Should().Be($"{AiActorService.RevisionNotCreatedMessage} {AiActorService.PlanPendingMessage}");
        plan.Status.Should().Be(AiActorPlanStatus.RevisionRequested, "the original plan was decided before the revision was generated");
        _store.Offline.TransactionIsolationLevels.Should().Equal(IsolationLevel.Serializable);
    }

    // A plan that became pending while the revision was generated refuses it, naming that plan.
    [Test]
    public async Task RequestPlanRevisionAsync_WhenAnotherPlanBecamePending_Is409NamingIt()
    {
        var actor = SeedActor();
        var plan = SeedPlan(actor, 40, DateTime.UtcNow.AddDays(-1));
        var service = BuildService(PlanJson, () => SeedPlan(actor, 41, DateTime.UtcNow));

        var act = () => service.RequestPlanRevisionAsync(new RequestRevisionOptions { PlanId = 40, UserId = "ext-ben", Feedback = "narrower" }, CancellationToken.None);

        (await act.Should().ThrowAsync<BeaconConflictException>()).Which.Message
            .Should().Be($"{AiActorService.RevisionNotCreatedMessage} {AiActorService.PlanPendingMessage} Plan 41.");
        _store.Plans.Select(x => x.Id).Should().BeEquivalentTo([40, 41]);
        plan.Status.Should().Be(AiActorPlanStatus.RevisionRequested);
        _audit.Of(SecurityAuditActions.AiPlanSubmitted).Should().BeEmpty();
    }

    [Test]
    public async Task RefineActorAsync_WhileAPlanIsPending_Is409BeforeTheLlmCall()
    {
        var actor = SeedActor();
        SeedPlan(actor, 40, DateTime.UtcNow.AddDays(-1));
        var llmCalls = 0;
        var service = BuildService(PlanJson, () => llmCalls++);

        var act = () => service.RefineActorAsync(ActorId, "also watch failed payments", CancellationToken.None);

        (await act.Should().ThrowAsync<BeaconConflictException>()).Which.Message
            .Should().Be($"{AiActorService.PlanPendingMessage} Plan 40.");
        llmCalls.Should().Be(0);
        _store.Executions.Should().BeEmpty();
        _store.Conversations.Should().BeEmpty();
        _store.Plans.Should().ContainSingle();
    }

    [Test]
    public async Task RefineActorAsync_NoApprovalRequired_RunsTheActionsAndProposesNoPlan()
    {
        var actor = SeedActor();
        actor.RequiresApproval = false;
        SeedPlan(actor, 40, DateTime.UtcNow.AddDays(-1));
        _queryService.Setup(x => x.DeleteQuery(OwnedQueryId, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var service = BuildService(ArchivesJson);

        var result = await service.RefineActorAsync(ActorId, "drop stale monitors", CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.PlanId.Should().BeNull();
        _store.Plans.Should().ContainSingle("the old plan does not hold back an actor that does not require approval");
        _queryService.Verify(x => x.DeleteQuery(OwnedQueryId, It.IsAny<CancellationToken>()), Times.Once);
        _audit.Of(SecurityAuditActions.AiPlanSubmitted).Should().BeEmpty();
    }

    // A refinement's plan keeps the feedback that asked for it, so a revision of the plan still carries it.
    [Test]
    public async Task RefineActorAsync_PlanKeepsTheFeedback_AndItsRevisionCarriesIt()
    {
        var actor = SeedActor();
        var service = BuildService(PlanJson);
        await service.RefineActorAsync(ActorId, "also watch failed payments", CancellationToken.None);
        var plan = _store.Plans.Should().ContainSingle().Subject;
        plan.UserInstruction.Should().Be("also watch failed payments");

        var revised = await service.RequestPlanRevisionAsync(new RequestRevisionOptions { PlanId = plan.Id, UserId = "ext-ben", Feedback = "only card payments" }, CancellationToken.None);

        revised.Success.Should().BeTrue(revised.ErrorMessage);
        var revision = _store.Plans.Single(x => x.Id == revised.PlanId);
        revision.UserInstruction.Should().StartWith("also watch failed payments").And.Contain("only card payments");
        revision.ParentPlanId.Should().Be(plan.Id);
        plan.Status.Should().Be(AiActorPlanStatus.RevisionRequested);
    }

    // A refinement's plan holds the target as the actor saw it; approving the locked one does not refine it.
    [Test]
    public async Task ExecuteThinkCycleAsync_RefinementsInThePlan_CarryTheirTargetAsTheActorSawIt()
    {
        SeedActor();
        SeedQueries();
        var service = BuildService(RefinementsJson);

        await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        var actions = JsonSerializer.Deserialize<List<ProposedAction>>(_store.Plans.Should().ContainSingle().Subject.ActionsJson, ReadOptions)!;
        actions.Should().BeEquivalentTo(
            new[]
            {
                new { ActionType = AiActorActionType.RefineQuery, TargetQueryId = (int?)OwnedQueryId, TargetQueryName = "Stale monitor", CurrentSql = "SELECT old", ProposedSql = "SELECT 2", IsLocked = false },
                new { ActionType = AiActorActionType.RefineQuery, TargetQueryId = (int?)LockedQueryId, TargetQueryName = "Locked monitor", CurrentSql = "SELECT kept", ProposedSql = "SELECT 4", IsLocked = true }
            },
            x => x.ExcludingMissingMembers().WithStrictOrdering());
    }

    [Test]
    public async Task ApprovePlanAsync_OfALockedQuerysRefinement_DoesNotRefineIt()
    {
        var actor = SeedActor();
        SeedQueries();
        var service = BuildService(LockedRefinementJson);
        await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);
        var plan = _store.Plans.Should().ContainSingle().Subject;
        plan.AiActor = actor;

        var result = await service.ApprovePlanAsync(new ApprovePlanOptions { PlanId = plan.Id, UserId = "ext-ben" }, CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Actions.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { ActionType = AiActorActionType.RefineQuery, Success = false, ErrorMessage = $"Query {LockedQueryId} is locked" },
            x => x.ExcludingMissingMembers());
        result.QueriesRefined.Should().Be(0);
        _queryService.VerifyNoOtherCalls();
        _store.Queries.Single(x => x.Id == LockedQueryId).Steps.Single().SqlValue.Should().Be("SELECT kept");
    }

    // The cycle's final save fails (here: the plan's submission event): the failure record that follows writes neither the
    // plan nor an event naming it.
    [Test]
    public async Task ExecuteThinkCycleAsync_WhenThePlansSaveFails_TheFailureRecordWritesNeitherThePlanNorItsEvent()
    {
        var actor = SeedActor();
        var service = BuildService(PlanJson, audit: new FailingSubmissionAudit(_store));

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.PlanId.Should().BeNull();
        actor.Status.Should().Be(AiActorStatus.Failed);
        _store.Executions.Should().ContainSingle().Which.Phase.Should().Be(AiActorExecutionPhase.Failed);
        _store.TrackedTypesAtSave.Last().Should().NotContain(typeof(AiActorPlan)).And.NotContain(typeof(SecurityAuditEvent));
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_WhenThePendingPlanExpired_ProposesANewPlan()
    {
        var actor = SeedActor();
        SeedPlan(actor, 40, Now.AddDays(-7));
        var service = BuildService(PlanJson, time: new FixedTimeProvider(Now), cooldownMinutes: 0);

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        _store.Plans.Should().HaveCount(2);
        var proposed = _store.Plans.Single(x => x.Id != 40);
        proposed.Should().BeEquivalentTo(new { Status = AiActorPlanStatus.PendingApproval, ProposedAt = Now }, x => x.ExcludingMissingMembers());
        result.PlanId.Should().Be(proposed.Id);
    }

    // Approving a cycle's plan runs exactly its actions, through the executor's guards: the actor's own query is archived,
    // the other actor's query is refused by the ownership guard.
    [Test]
    public async Task ApprovePlanAsync_OfAPlanACycleProposed_RunsExactlyItsActionsThroughTheGuards()
    {
        var actor = SeedActor();
        _store.Queries.Add(new Query { Id = OtherActorsQueryId, Name = "Someone else's", AiActorId = ActorId + 1 });
        _queryService.Setup(x => x.DeleteQuery(OwnedQueryId, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var service = BuildService(ArchivesJson);
        var proposedBy = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);
        _queryService.VerifyNoOtherCalls();
        var plan = _store.Plans.Should().ContainSingle().Subject;
        plan.AiActor = actor;

        var result = await service.ApprovePlanAsync(new ApprovePlanOptions { PlanId = plan.Id, UserId = "ext-ben" }, CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Actions.Select(x => (x.ActionType, x.Success, x.TargetQueryId)).Should().Equal(
            (AiActorActionType.ArchiveQuery, true, (int?)OwnedQueryId),
            (AiActorActionType.ArchiveQuery, false, (int?)OtherActorsQueryId));
        result.Actions[1].ErrorMessage.Should().Contain("Not owned by this actor");
        _queryService.Verify(x => x.DeleteQuery(OwnedQueryId, It.IsAny<CancellationToken>()), Times.Once);
        _queryService.VerifyNoOtherCalls();
        plan.Status.Should().Be(AiActorPlanStatus.Executed);
        actor.TotalTokensUsed.Should().Be(30, "approving a cycle's plan does not count the cycle's tokens again");
        _store.Executions.Should().HaveCount(2).And.Contain(x => x.Id != proposedBy.ExecutionId && x.AiActorPlanId == plan.Id);
    }

    [Test]
    public async Task ApprovePlanAsync_OfAnExpiredPlan_Is409AndRunsNothing()
    {
        var actor = SeedActor();
        var plan = SeedPlan(actor, 40, Now.AddDays(-8));
        var service = BuildService(PlanJson, time: new FixedTimeProvider(Now));

        var act = () => service.ApprovePlanAsync(new ApprovePlanOptions { PlanId = 40, UserId = "ext-ben" }, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(AiActorService.PlanExpiredMessage);
        plan.Status.Should().Be(AiActorPlanStatus.PendingApproval);
        plan.ReviewedByUserId.Should().BeNull();
        _store.Executions.Should().BeEmpty();
        _audit.Events.Should().BeEmpty();
        AssertNothingExecuted();
    }

    [Test]
    public async Task RequestPlanRevisionAsync_OfAnExpiredPlan_Is409AndGeneratesNothing()
    {
        var actor = SeedActor();
        var plan = SeedPlan(actor, 40, Now.AddDays(-8));
        var llmCalls = 0;
        var service = BuildService(PlanJson, () => llmCalls++, time: new FixedTimeProvider(Now));

        var act = () => service.RequestPlanRevisionAsync(new RequestRevisionOptions { PlanId = 40, UserId = "ext-ben", Feedback = "narrower" }, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(AiActorService.PlanExpiredMessage);
        plan.Status.Should().Be(AiActorPlanStatus.PendingApproval);
        llmCalls.Should().Be(0);
        _store.Plans.Should().ContainSingle();
    }

    [Test]
    public async Task RejectPlanAsync_OfAnExpiredPlan_Is409()
    {
        var actor = SeedActor();
        var plan = SeedPlan(actor, 40, Now.AddDays(-8));
        var service = BuildService(PlanJson, time: new FixedTimeProvider(Now));

        var act = () => service.RejectPlanAsync(new RejectPlanOptions { PlanId = 40, UserId = "ext-ben", Reason = "too broad" }, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(AiActorService.PlanExpiredMessage);
        plan.Status.Should().Be(AiActorPlanStatus.PendingApproval);
        _audit.Events.Should().BeEmpty();
    }

    // Expiry is exactly PlanExpiryDays after the plan was proposed.
    [TestCase(0, true)]
    [TestCase(-1, false)]
    public async Task ApprovePlanAsync_AtTheExpiryBoundary(int secondsAfterExpiry, bool expired)
    {
        var actor = SeedActor();
        var plan = SeedPlan(actor, 40, Now.AddDays(-7).AddSeconds(-secondsAfterExpiry));
        plan.ActionsJson = "[]";
        var service = BuildService(PlanJson, time: new FixedTimeProvider(Now));

        var act = () => service.ApprovePlanAsync(new ApprovePlanOptions { PlanId = 40, UserId = "ext-ben" }, CancellationToken.None);

        if (expired)
        {
            await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(AiActorService.PlanExpiredMessage);
            return;
        }

        (await act()).Success.Should().BeTrue();
        plan.Status.Should().Be(AiActorPlanStatus.Executed);
    }

    [Test]
    public async Task ApprovePlanAsync_WithinTheConfiguredExpiry_IsDecided()
    {
        var actor = SeedActor();
        var plan = SeedPlan(actor, 40, Now.AddDays(-8));
        plan.ActionsJson = "[]";
        var service = BuildService(PlanJson, time: new FixedTimeProvider(Now), planExpiryDays: 9);

        var result = await service.ApprovePlanAsync(new ApprovePlanOptions { PlanId = 40, UserId = "ext-ben" }, CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        plan.Status.Should().Be(AiActorPlanStatus.Executed);
    }

    [Test]
    public async Task GetPendingPlansAsync_HidesExpiredPlans()
    {
        var actor = SeedActor();
        SeedPlan(actor, 40, Now.AddDays(-8));
        SeedPlan(actor, 41, Now.AddDays(-1));
        var service = BuildService(PlanJson, time: new FixedTimeProvider(Now));

        var pending = await service.GetPendingPlansAsync(ActorId, CancellationToken.None);

        pending.Select(x => x.PlanId).Should().Equal(41);
        pending.Single().ExpiresAt.Should().Be(Now.AddDays(6));
    }

    [TestCase(-6, false)]
    [TestCase(-7, true)]
    public async Task GetAiActorPlan_SaysWhenThePlanExpires(int proposedDaysAgo, bool expired)
    {
        var actor = SeedActor();
        var plan = SeedPlan(actor, 40, Now.AddDays(proposedDaysAgo));
        var service = new Mock<IAiActorServiceExtended>();
        service.Setup(x => x.GetPlanAsync(40, It.IsAny<CancellationToken>())).ReturnsAsync(plan);
        var handler = new GetAiActorPlanHandler(
            service.Object,
            Mock.Of<IOptionsMonitor<AiActorOptions>>(x => x.CurrentValue == new AiActorOptions()),
            new FixedTimeProvider(Now));

        var result = await handler.Handle(new GetAiActorPlanQuery { PlanId = 40 }, CancellationToken.None);

        result!.ExpiresAt.Should().Be(Now.AddDays(proposedDaysAgo + 7));
        result.IsExpired.Should().Be(expired);
    }

    [Test]
    public async Task GeneratePlanAsync_WithNoActionOfAKnownType_CreatesNoPlanAndFails()
    {
        SeedActor();
        var service = BuildService(NoActionsJson);

        var result = await service.GeneratePlanAsync(new GeneratePlanOptions { ActorId = ActorId }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(AiActorService.NoActionsProposedMessage);
        _store.Plans.Should().BeEmpty();
        _audit.Events.Should().BeEmpty();
    }

    [Test]
    public async Task GeneratePlanAsync_StoresOnlyTheActionsOfAKnownType_ProposedNow()
    {
        SeedActor();
        var service = BuildService(MalformedPlanJson, time: new FixedTimeProvider(Now));

        var result = await service.GeneratePlanAsync(new GeneratePlanOptions { ActorId = ActorId }, CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        var plan = _store.Plans.Should().ContainSingle().Subject;
        plan.ProposedAt.Should().Be(Now);
        JsonSerializer.Deserialize<List<ProposedAction>>(plan.ActionsJson, ReadOptions)!
            .Select(x => x.ActionType).Should().Equal(AiActorActionType.CreateQuery);
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_CancelledMidCycle_ClosesOnlyTheExecutionRowWithALiveTokenAndRethrows()
    {
        var actor = SeedActor();
        using var cancellation = new CancellationTokenSource();
        var service = BuildService(PlanJson, onLlmCall: () => cancellation.Cancel());

        var act = () => service.ExecuteThinkCycleAsync(ActorId, cancellationToken: cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        actor.Status.Should().Be(AiActorStatus.Active, "a shutdown must not stop the actor");
        actor.LastError.Should().BeNull();
        AssertCancelledExecution();
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_CancelledMidCycle_DoesNotWriteWhatTheCycleHadChangedButNotSaved()
    {
        var actor = SeedActor();
        using var cancellation = new CancellationTokenSource();
        var service = BuildService(
            PlanJson,
            onLlmCall: () =>
            {
                var context = _store.Contexts.First();
                context.Attach(actor).Property(x => x.LastError).CurrentValue = "unsaved";
                cancellation.Cancel();
            });

        var act = () => service.ExecuteThinkCycleAsync(ActorId, cancellationToken: cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _store.TrackedEntitiesAtSave.Last().Should().Be(0, "the unsaved change to the actor must not be written by the cancelled cycle");
    }

    [Test]
    public async Task RefineActorAsync_CancelledMidCycle_ClosesOnlyTheExecutionRowWithALiveTokenAndRethrows()
    {
        var actor = SeedActor();
        using var cancellation = new CancellationTokenSource();
        var service = BuildService(PlanJson, onLlmCall: () => cancellation.Cancel());

        var act = () => service.RefineActorAsync(ActorId, "also watch failed payments", cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        actor.Status.Should().Be(AiActorStatus.Active);
        AssertCancelledExecution();
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_RealFailure_StillSavesTheFailedStatusWithALiveToken()
    {
        var actor = SeedActor();
        var service = BuildService(PlanJson, onLlmCall: () => throw new InvalidOperationException("model unavailable"));

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("model unavailable");
        actor.Status.Should().Be(AiActorStatus.Failed);
        _store.Executions.Should().ContainSingle().Which.Phase.Should().Be(AiActorExecutionPhase.Failed);
        _store.SaveTokensCancelled.Last().Should().BeFalse();
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_WhenTheFailureSaveFails_KeepsTheOriginalFailure()
    {
        SeedActor();
        var service = BuildService(
            PlanJson,
            onLlmCall: () =>
            {
                _store.FailSaves = true;

                throw new InvalidOperationException("model unavailable");
            });

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("model unavailable");
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_CancelledWhenTheFailureSaveFails_RethrowsTheCancellation()
    {
        SeedActor();
        using var cancellation = new CancellationTokenSource();
        var service = BuildService(
            PlanJson,
            onLlmCall: () =>
            {
                _store.FailSaves = true;
                cancellation.Cancel();
            });

        var act = () => service.ExecuteThinkCycleAsync(ActorId, cancellationToken: cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task RefineActorAsync_RequiresApproval_StoresProposalsAsOnePendingPlanAndExecutesNothing()
    {
        SeedActor();
        var service = BuildService(PlanJson);

        var result = await service.RefineActorAsync(ActorId, "also watch failed payments", CancellationToken.None);

        result.Success.Should().BeTrue();
        _store.Conversations.Should().HaveCount(2, "the feedback and the LLM reply are both stored");
        var execution = SingleCompletedExecution();
        AssertProposedAndNothingExecuted(execution);
        result.PlanId.Should().Be(AssertOnePendingPlan(execution).Id);
    }

    [Test]
    public async Task CreateActorAsync_NoApprovalRequired_InitialSetupRunsTheActionsAndProposesNoPlan()
    {
        _queryService
            .Setup(x => x.PreviewQuery(0, It.IsAny<QueryDraft>(), It.IsAny<ListRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryPreviewResult { Success = true });
        var service = BuildService(CreateOnlyJson);

        var actor = await service.CreateActorAsync(
            new CreateAiActorOptions
            {
                Name = "Payments watcher",
                Instructions = "Watch failed payments",
                DataSourceId = DataSourceId,
                ActivateImmediately = true,
                RequiresApproval = false
            },
            CancellationToken.None);

        actor.RequiresApproval.Should().BeFalse();
        _store.Actors.Should().ContainSingle().Which.RequiresApproval.Should().BeFalse();
        _store.Plans.Should().BeEmpty();
        _queriesSet.Verify(x => x.Add(It.Is<Query>(y => y.Name == "Failed payments" && y.AiActorId == actor.Id)), Times.Once);
        SingleCompletedExecution().QueriesCreated.Should().Be(1);
        _audit.Of(SecurityAuditActions.AiPlanSubmitted).Should().BeEmpty();
        _audit.Of(SecurityAuditActions.AiActorCreated).Should().ContainSingle()
            .Which.DetailsJson.Should().Be($"{{\"dataSourceId\":{DataSourceId},\"requiresApproval\":false}}");
    }

    [Test]
    public async Task CreateActorAsync_InitialSetup_RequiresApprovalByDefault_StoresProposalsAsOnePendingPlanAndExecutesNothing()
    {
        var service = BuildService(PlanJson);

        var actor = await service.CreateActorAsync(
            new CreateAiActorOptions
            {
                Name = "Payments watcher",
                Instructions = "Watch failed payments",
                DataSourceId = DataSourceId,
                ActivateImmediately = true
            },
            CancellationToken.None);

        actor.RequiresApproval.Should().BeTrue("a new actor proposes until someone opts it out");
        actor.Status.Should().Be(AiActorStatus.Active, "initial setup must complete, not fail the actor");
        actor.LastThinkTime.Should().BeNull("the initial setup does not start the think cooldown");
        var execution = SingleCompletedExecution();
        AssertProposedAndNothingExecuted(execution);
        AssertOnePendingPlan(execution, actor.Id);
        _audit.Of(SecurityAuditActions.AiActorCreated).Should().ContainSingle()
            .Which.DetailsJson.Should().Be($"{{\"dataSourceId\":{DataSourceId},\"requiresApproval\":true}}");
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_MalformedActions_FailOneByOneAndTheCycleCompletes()
    {
        var actor = SeedActor();
        var service = BuildService(MalformedPlanJson);

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        result.Success.Should().BeTrue();
        actor.Status.Should().Be(AiActorStatus.Active);
        actor.LastError.Should().BeNull();

        var actions = StoredActions(SingleCompletedExecution());
        actions.Should().HaveCount(4);
        actions.Take(3).Should().OnlyContain(x => !x.Proposed && !x.Success && !string.IsNullOrEmpty(x.ErrorMessage));
        actions[0].ErrorMessage.Should().Be("Unknown action type");
        actions[1].ErrorMessage.Should().Be("Unknown action type");
        actions[3].Proposed.Should().BeTrue("the actions after a malformed one are still processed");
        actions[3].ActionType.Should().Be(AiActorActionType.CreateQuery);
        var plan = _store.Plans.Should().ContainSingle().Subject;
        JsonSerializer.Deserialize<List<ProposedAction>>(plan.ActionsJson, ReadOptions)!
            .Select(x => x.ActionType).Should().Equal(AiActorActionType.CreateQuery);
        AssertNothingExecuted();
    }

    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task ExecuteThinkCycleAsync_WithinCooldown_SkipsAndSaysHowLongIsLeft()
    {
        var actor = SeedActor();
        actor.LastThinkTime = Now.AddMinutes(-10);
        var llmCalls = 0;
        var service = BuildService(PlanJson, () => llmCalls++, time: new FixedTimeProvider(Now));

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain(AiActorService.ThinkCooldownMessage).And.Contain("5 minute(s)");
        llmCalls.Should().Be(0);
        _store.Executions.Should().BeEmpty();
        actor.LastThinkTime.Should().Be(Now.AddMinutes(-10));
    }

    [Test]
    public async Task ExecuteThinkCycleBackgroundAsync_AsksTheModelInTheBackgroundLane()
    {
        SeedActor();
        var requests = new List<LlmRequest>();
        var service = BuildService(PlanJson, llmRequests: requests);

        await service.ExecuteThinkCycleBackgroundAsync(ActorId, null, CancellationToken.None);

        requests.Should().ContainSingle().Which.Priority.Should().Be(LlmRequestPriority.Background);
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_AsksTheModelInTheInteractiveLane()
    {
        SeedActor();
        var requests = new List<LlmRequest>();
        var service = BuildService(PlanJson, llmRequests: requests);

        await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        requests.Should().ContainSingle().Which.Priority.Should().Be(LlmRequestPriority.Interactive);
    }

    [Test]
    public async Task ExecuteThinkCycleBackgroundAsync_WithinCooldown_SkipsAndLogsOnlyTheActorIdAtDebug()
    {
        var actor = SeedActor();
        actor.LastThinkTime = Now.AddMinutes(-1);
        var llmCalls = 0;
        var service = BuildService(PlanJson, () => llmCalls++, time: new FixedTimeProvider(Now));

        await service.ExecuteThinkCycleBackgroundAsync(ActorId, null, CancellationToken.None);

        llmCalls.Should().Be(0);
        _store.Executions.Should().BeEmpty();
        _logger.Invocations
            .Where(x => x.Arguments[0] is LogLevel.Debug)
            .Should()
            .ContainSingle(x => x.Arguments[2].ToString()!.Contains("within the think cooldown") && x.Arguments[2].ToString()!.Contains(ActorId.ToString()));
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_AfterCooldown_RunsAndCompletionDoesNotWriteTheLastThinkTime()
    {
        var actor = SeedActor();
        actor.LastThinkTime = Now.AddMinutes(-16);
        var service = BuildService(PlanJson, time: new FixedTimeProvider(Now));

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        actor.ThinkCount.Should().Be(1);
        actor.LastThinkTime.Should().Be(Now.AddMinutes(-16), "only the claim writes it");
        SingleCompletedExecution().CompletedAt.Should().NotBeNull();
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_NewerClaimLandsDuringTheCycle_KeepsTheNewerTimestamp()
    {
        var actor = SeedActor();
        var newer = Now.AddMinutes(20);
        var service = BuildService(PlanJson, () => actor.LastThinkTime = newer, time: new FixedTimeProvider(Now));

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        actor.LastThinkTime.Should().Be(newer);
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_CooldownOff_RunsAgainImmediately()
    {
        var actor = SeedActor();
        actor.LastThinkTime = Now;
        var service = BuildService(PlanJson, time: new FixedTimeProvider(Now), cooldownMinutes: 0);

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_TwoCyclesAtOnce_OnlyTheClaimHolderRuns()
    {
        SeedActor();
        var llmCalls = 0;
        var claim = new SingleWinnerThinkClaim();
        var first = BuildService(PlanJson, () => llmCalls++, claim, new FixedTimeProvider(Now));
        var second = BuildService(PlanJson, () => llmCalls++, claim, new FixedTimeProvider(Now));

        var results = await Task.WhenAll(
            first.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None),
            second.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None));

        results.Count(x => x.Success).Should().Be(1);
        llmCalls.Should().Be(1);
        _store.Executions.Should().ContainSingle();
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_CooldownOff_DoesNotTouchTheClaim()
    {
        SeedActor();
        var claim = new Mock<IAiActorThinkClaim>(MockBehavior.Strict);
        var service = BuildService(PlanJson, claim: claim.Object, time: new FixedTimeProvider(Now), cooldownMinutes: 0);

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_ActorArchivedBeforeTheClaim_SaysItIsUnavailable()
    {
        SeedActor();
        var claim = new Mock<IAiActorThinkClaim>();
        claim
            .Setup(x => x.TryClaimAsync(ActorId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ThinkClaimResult.Missing);
        var service = BuildService(PlanJson, claim: claim.Object, time: new FixedTimeProvider(Now));

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(AiActorService.ActorUnavailableMessage);
    }

    [Test]
    public void AiActorOptionsValidator_RejectsACooldownOutOfRange()
    {
        var validator = new AiActorOptionsValidator();

        validator.Validate(null, new AiActorOptions { ThinkCooldownMinutes = -1 }).Failed.Should().BeTrue();
        validator.Validate(null, new AiActorOptions { ThinkCooldownMinutes = AiActorOptions.MaxThinkCooldownMinutes + 1 }).Failed.Should().BeTrue();
        validator.Validate(null, new AiActorOptions { ThinkCooldownMinutes = 0 }).Succeeded.Should().BeTrue();
        validator.Validate(null, new AiActorOptions()).Succeeded.Should().BeTrue();
    }

    [Test]
    public void AiActorOptionsValidator_RejectsAPlanExpiryOutOfRange()
    {
        var validator = new AiActorOptionsValidator();

        new AiActorOptions().PlanExpiryDays.Should().Be(7);
        validator.Validate(null, new AiActorOptions { PlanExpiryDays = 0 }).Failed.Should().BeTrue();
        validator.Validate(null, new AiActorOptions { PlanExpiryDays = AiActorOptions.MaxPlanExpiryDays + 1 }).Failed.Should().BeTrue();
        validator.Validate(null, new AiActorOptions { PlanExpiryDays = 1 }).Succeeded.Should().BeTrue();
        validator.Validate(null, new AiActorOptions { PlanExpiryDays = AiActorOptions.MaxPlanExpiryDays }).Succeeded.Should().BeTrue();
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class SingleWinnerThinkClaim : IAiActorThinkClaim
    {
        private int _taken;

        public Task<ThinkClaimResult> TryClaimAsync(int actorId, DateTime now, DateTime threshold, CancellationToken cancellationToken)
            => Task.FromResult(Interlocked.Exchange(ref _taken, 1) == 0 ? ThinkClaimResult.Claimed : ThinkClaimResult.Held);
    }

    private ActorEntity SeedActor()
    {
        var actor = new ActorEntity
        {
            Id = ActorId,
            Name = "Payments watcher",
            Instructions = "Watch failed payments",
            DataSourceId = DataSourceId,
            Status = AiActorStatus.Active,
            RequiresApproval = true
        };
        _store.Actors.Add(actor);

        return actor;
    }

    // The actor's own query gets SQL, and a second, locked query of its own.
    private static DbUpdateException SerializationFailure() =>
        new("save failed", new PostgresException("could not serialize access", "ERROR", "ERROR", "40001"));

    private void SeedQueries()
    {
        _store.Queries.Single(x => x.Id == OwnedQueryId).Steps = [new QueryStep { QueryId = OwnedQueryId, DataSourceId = DataSourceId, StepOrder = 1, SqlValue = "SELECT old" }];
        _store.Queries.Add(
            new Query
            {
                Id = LockedQueryId,
                Name = "Locked monitor",
                AiActorId = ActorId,
                IsLocked = true,
                Steps = [new QueryStep { QueryId = LockedQueryId, DataSourceId = DataSourceId, StepOrder = 1, SqlValue = "SELECT kept" }]
            });
    }

    private AiActorPlan SeedPlan(ActorEntity actor, int id, DateTime proposedAt)
    {
        var plan = new AiActorPlan
        {
            Id = id,
            AiActorId = ActorId,
            AiActor = actor,
            Status = AiActorPlanStatus.PendingApproval,
            Analysis = "earlier",
            ActionsJson = "[]",
            ProposedAt = proposedAt
        };
        _store.Plans.Add(plan);

        return plan;
    }

    // The cycle's proposals wait as one PendingApproval plan in the shape ApprovePlanAsync executes, its submission on
    // record, and the execution names it.
    private AiActorPlan AssertOnePendingPlan(AiActorExecution execution, int actorId = ActorId)
    {
        var plan = _store.Plans.Should().ContainSingle().Subject;
        plan.Id.Should().BePositive();
        plan.Should().BeEquivalentTo(
            new { AiActorId = actorId, Status = AiActorPlanStatus.PendingApproval, Analysis = "one stale monitor, one gap", Model = "stub", Version = 1, ParentPlanId = (int?)null, TokensUsed = 0, EstimatedCost = 0m },
            x => x.ExcludingMissingMembers());
        var actions = JsonSerializer.Deserialize<List<ProposedAction>>(plan.ActionsJson, ReadOptions)!;
        actions.Select(x => x.ActionType).Should().Equal(AiActorActionType.ArchiveQuery, AiActorActionType.CreateQuery);
        actions[0].Parameters["queryId"].ToString().Should().Be(OwnedQueryId.ToString());
        actions[1].Parameters["sql"].ToString().Should().Be("SELECT 1");
        execution.DetailedAnalysis.Should().Contain($"plan {plan.Id}");
        var submitted = _audit.Of(SecurityAuditActions.AiPlanSubmitted).Should().ContainSingle().Subject;
        submitted.ResourceId.Should().Be(plan.Id.ToString());
        submitted.DetailsJson.Should().Be($"{{\"aiActorId\":{actorId},\"parentPlanId\":null,\"version\":1,\"proposedActionCount\":2}}");
        SecurityAuditTestKit.AssertNoPii(submitted, "one stale monitor", "Failed payments", "SELECT 1", "also watch failed payments");

        return plan;
    }

    private void AssertCancelledExecution()
    {
        var execution = _store.Executions.Should().ContainSingle().Subject;
        execution.Phase.Should().Be(AiActorExecutionPhase.Failed);
        execution.ErrorMessage.Should().Be("cancelled");
        _store.SaveTokensCancelled.Should().NotBeEmpty();
        _store.SaveTokensCancelled.Last().Should().BeFalse("the failure record must not be written with the cancelled token");
    }

    private AiActorExecution SingleCompletedExecution()
    {
        var execution = _store.Executions.Should().ContainSingle().Subject;
        execution.Phase.Should().Be(AiActorExecutionPhase.Completed, "the cycle must not fail (error: {0})", execution.ErrorMessage);

        return execution;
    }

    private void AssertProposedAndNothingExecuted(AiActorExecution execution)
    {
        var actions = StoredActions(execution);

        actions.Select(x => x.ActionType).Should().Equal(AiActorActionType.ArchiveQuery, AiActorActionType.CreateQuery);
        actions.Should().OnlyContain(x => x.Proposed && !x.Success);
        actions[0].TargetQueryId.Should().Be(OwnedQueryId);
        actions[1].QueryName.Should().Be("Failed payments");
        actions[1].SqlQuery.Should().Be("SELECT 1");
        execution.QueriesCreated.Should().Be(0);
        AssertNothingExecuted();
    }

    private void AssertNothingExecuted()
    {
        _queryService.VerifyNoOtherCalls();
        _subscriptionService.VerifyNoOtherCalls();
        _queriesSet.Verify(x => x.Add(It.IsAny<Query>()), Times.Never);
        _subscriptionsSet.Verify(x => x.Add(It.IsAny<Subscription>()), Times.Never);
        _store.Queries.Should().ContainSingle().Which.ArchivedTime.Should().BeNull();
    }

    private static List<AiActorAction> StoredActions(AiActorExecution execution)
    {
        execution.ActionsJson.Should().NotBeNullOrEmpty();

        return JsonSerializer.Deserialize<List<AiActorAction>>(execution.ActionsJson!, ReadOptions)!;
    }

    private AiActorService BuildService(
        string llmContent,
        Action? onLlmCall = null,
        IAiActorThinkClaim? claim = null,
        TimeProvider? time = null,
        int cooldownMinutes = 15,
        int planExpiryDays = 7,
        ISecurityAuditRecorder? audit = null,
        List<LlmRequest>? llmRequests = null)
    {
        var sets = new Dictionary<Type, object>
        {
            [typeof(ActorEntity)] = BuildSet(_store.Actors).Object,
            [typeof(DataSource)] = BuildSet(_store.DataSources).Object,
            [typeof(Query)] = _queriesSet.Object,
            [typeof(Subscription)] = _subscriptionsSet.Object,
            [typeof(AiActorExecution)] = BuildSet(_store.Executions).Object,
            [typeof(AiActorConversation)] = BuildSet(_store.Conversations).Object,
            [typeof(AiActorPlan)] = BuildSet(_store.Plans).Object
        };

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                () =>
                {
                    var context = new CycleContext(sets, _store);
                    _store.Contexts.Add(context);

                    return context;
                });

        var llm = new Mock<ILlmProvider>(MockBehavior.Strict);
        llm
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns<LlmRequest, CancellationToken>((request, token) =>
            {
                llmRequests?.Add(request);
                onLlmCall?.Invoke();
                token.ThrowIfCancellationRequested();

                return Task.FromResult(
                    new LlmResponse
                    {
                        Content = llmContent,
                        Model = "stub",
                        InputTokens = 30,
                        Cost = 0.01m
                    });
            });

        var metadata = new Mock<IDatabaseMetadataService>(MockBehavior.Strict);
        metadata
            .Setup(x => x.GetMetadataAsync(DataSourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadataSnapshot(DataSourceId, DatabaseEngineType.PostgreSQL, [], DateTime.UtcNow));

        return new AiActorService(
            factory.Object,
            llm.Object,
            metadata.Object,
            _queryService.Object,
            _subscriptionService.Object,
            Mock.Of<IBeaconScheduler>(MockBehavior.Strict),
            Mock.Of<IRunAsScope>(),
            TestProjectAccess.ExecutionAllowed(),
            Mock.Of<IStepSqlSaveCheck>(),
            Mock.Of<IQueryProposalCheck>(MockBehavior.Strict),
            Mock.Of<IQueryVersionService>(),
            new Beacon.Core.BeaconConfiguration(),
            audit ?? _audit,
            SubscriptionScheduleCheck.Default,
            _logger.Object,
            Mock.Of<IOptionsMonitor<AiActorOptions>>(x => x.CurrentValue == new AiActorOptions { ThinkCooldownMinutes = cooldownMinutes, PlanExpiryDays = planExpiryDays }),
            time,
            claim ?? new GrantingThinkClaim());
    }

    private static Mock<DbSet<T>> BuildSet<T>(List<T> rows) where T : class
    {
        var data = rows.AsQueryable();
        var set = new Mock<DbSet<T>>();
        set.As<IAsyncEnumerable<T>>()
            .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(() => new TestAsyncEnumerator<T>(data.GetEnumerator()));
        set.As<IQueryable<T>>().Setup(x => x.Provider).Returns(new TestAsyncQueryProvider<T>(data.Provider));
        set.As<IQueryable<T>>().Setup(x => x.Expression).Returns(data.Expression);
        set.As<IQueryable<T>>().Setup(x => x.ElementType).Returns(data.ElementType);
        set.As<IQueryable<T>>().Setup(x => x.GetEnumerator()).Returns(() => data.GetEnumerator());
        set
            .Setup(x => x.Add(It.IsAny<T>()))
            .Callback<T>(x => rows.Add(x));

        return set;
    }

    /// <summary>The rows the cycle reads and the rows it adds; shared by every context the factory hands out.</summary>
    private sealed class Store
    {
        public List<ActorEntity> Actors { get; } = [];

        public List<DataSource> DataSources { get; } = [];

        public List<Query> Queries { get; } = [];

        public List<Subscription> Subscriptions { get; } = [];

        public List<AiActorExecution> Executions { get; } = [];

        public List<AiActorConversation> Conversations { get; } = [];

        public List<AiActorPlan> Plans { get; } = [];

        public List<bool> SaveTokensCancelled { get; } = [];

        public bool FailSaves { get; set; }

        public List<BeaconContext> Contexts { get; } = [];

        public List<int> TrackedEntitiesAtSave { get; } = [];

        public int NextId { get; set; } = 100;

        public List<List<Type>> TrackedTypesAtSave { get; } = [];

        // Transactions are stand-ins recorded here; nothing connects.
        public SqlCapture Offline { get; } = new();
    }

    // Saves the cycle's change, stages the plan's events and the plan as the real recorder's context would hold them, then
    // fails the events' save.
    private sealed class FailingSubmissionAudit(Store store, Exception? failure = null) : ISecurityAuditRecorder
    {
        public Task StageAsync(BeaconContext context, SecurityAuditEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task SaveWithEventsAsync(BeaconContext context, Func<IEnumerable<SecurityAuditEntry>> events, CancellationToken cancellationToken)
        {
            await context.SaveChangesAsync(cancellationToken);
            foreach (var entry in events())
            {
                context.Add(
                    new SecurityAuditEvent
                    {
                        Category = "approvals",
                        Action = entry.Action,
                        Outcome = "success",
                        ActorKind = SecurityAuditActorKinds.User,
                        KeyId = "test",
                        Hmac = "test"
                    });
            }

            foreach (var plan in store.Plans)
            {
                context.Entry(plan).State = EntityState.Added;
            }

            throw failure ?? new DbUpdateException("the events could not be saved");
        }

        // A decision on a plan: its change runs, its events are not kept.
        public Task<T> SaveWithEventsAsync<T>(BeaconContext context, Func<Task<T>> change, Func<T, IEnumerable<SecurityAuditEntry>> events, CancellationToken cancellationToken) =>
            change();

        public Task RecordAsync(SecurityAuditEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;

        public string CallerHashOf(string tenantOrIssuer, string subject) => subject;
    }

    private sealed class CycleContext(IReadOnlyDictionary<Type, object> sets, Store store) : BeaconContext(OptionsFor(store), "beacon")
    {
        private static DbContextOptions<CycleContext> OptionsFor(Store store) =>
            new DbContextOptionsBuilder<CycleContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(store.Offline.OfflineInterceptors())
                .Options;

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (sets.TryGetValue(typeof(TEntity), out var set))
            {
                return (DbSet<TEntity>)set;
            }

            return base.Set<TEntity>();
        }

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            store.SaveTokensCancelled.Add(cancellationToken.IsCancellationRequested);
            store.TrackedEntitiesAtSave.Add(ChangeTracker.Entries().Count());
            store.TrackedTypesAtSave.Add(ChangeTracker.Entries().Select(x => x.Entity.GetType()).ToList());
            if (store.FailSaves)
            {
                throw new InvalidOperationException("database unavailable");
            }

            // As the database would: rows added since the last save get their ids.
            foreach (var actor in store.Actors.Where(x => x.Id == 0))
            {
                actor.Id = store.NextId++;
            }

            foreach (var plan in store.Plans.Where(x => x.Id == 0))
            {
                plan.Id = store.NextId++;
            }

            foreach (var execution in store.Executions.Where(x => x.Id == 0))
            {
                execution.Id = store.NextId++;
            }

            return Task.FromResult(0);
        }
    }
}

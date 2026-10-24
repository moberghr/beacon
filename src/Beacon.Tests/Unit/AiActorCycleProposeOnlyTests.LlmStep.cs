using Beacon.AI.Handlers.AiActors;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.Ai.AiActor.Models;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.AiActors;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Ai;
using Beacon.Core.Models.Queries;
using Beacon.Core.SecurityAudit;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using ActorEntity = Beacon.Core.Data.Entities.AiActor;

namespace Beacon.Tests.Unit;

/// <summary>
/// The model step every cycle shares — the completion asked of the model, how its reply is read, and how the planned
/// actions are executed or proposed and counted — pinned for each producer: the think cycle, a refinement, the initial
/// setup and a generated plan.
/// </summary>
public partial class AiActorCycleProposeOnlyTests
{
    public enum CycleKind
    {
        Think,
        Refine,
        Setup
    }

    private const string Feedback = "also watch failed payments";

    private const string NotJsonReply = "nothing to change today";

    private const string InvalidJsonReply = "{ this is not json }";

    private const string NotifyJson = """
        { "analysis": "spike", "findings": ["failures doubled"], "actions": [], "shouldNotify": true, "notificationReason": "failures doubled" }
        """;

    private const string OtherActorsArchiveJson = """
        {
          "analysis": "another monitor",
          "findings": [],
          "actions": [
            { "actionType": "ARCHIVE_QUERY", "reasoning": "stale", "parameters": { "queryId": 2 } }
          ],
          "shouldNotify": false
        }
        """;

    [Test]
    public async Task ExecuteThinkCycleAsync_AsksTheModelWithTheThinkPrompt()
    {
        var actor = SeedActor();
        var requests = new List<LlmRequest>();
        var service = BuildService(PlanJson, llmRequests: requests);
        // The prompt shows the actor before the cycle counts itself.
        var expected = AiActorPrompts.BuildThinkCyclePrompt(actor, SchemaContext(), [OwnedQueryContext()], null, null);

        await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        AssertSingleRequest(requests, AiActorPrompts.ThinkCycleSystemPrompt, expected, LlmOperations.ActorThink);
    }

    [Test]
    public async Task RefineActorAsync_AsksTheModelWithTheRefinementPrompt()
    {
        var actor = SeedActor();
        var requests = new List<LlmRequest>();
        var service = BuildService(PlanJson, llmRequests: requests);

        await service.RefineActorAsync(ActorId, Feedback, CancellationToken.None);

        AssertSingleRequest(
            requests,
            AiActorPrompts.RefinementSystemPrompt,
            AiActorPrompts.BuildRefinementPrompt(actor, SchemaContext(), [OwnedQueryContext()], Feedback),
            LlmOperations.ActorRefine);
    }

    [Test]
    public async Task CreateActorAsync_InitialSetup_AsksTheModelWithTheSetupPrompt()
    {
        var requests = new List<LlmRequest>();
        var service = BuildService(PlanJson, llmRequests: requests);

        var actor = await CreateActorAsync(service, requiresApproval: true);

        AssertSingleRequest(
            requests,
            AiActorPrompts.InitialSetupSystemPrompt,
            AiActorPrompts.BuildInitialSetupPrompt(actor, SchemaContext()),
            LlmOperations.ActorSetup);
    }

    [Test]
    public async Task GeneratePlanAsync_WithAnInstruction_AsksTheModelWithTheRefinementPromptNamingTheLockedQueries()
    {
        var actor = SeedActor();
        SeedQueries();
        var requests = new List<LlmRequest>();
        var service = BuildService(PlanJson, llmRequests: requests);

        await service.GeneratePlanAsync(new GeneratePlanOptions { ActorId = ActorId, UserInstruction = Feedback }, CancellationToken.None);

        var queries = new List<AiActorPrompts.QueryContext>
        {
            OwnedQueryContext("SELECT old"),
            new() { QueryId = LockedQueryId, QueryName = "Locked monitor", Sql = "SELECT kept" }
        };
        AssertSingleRequest(
            requests,
            AiActorPrompts.RefinementSystemPrompt,
            AiActorPrompts.BuildRefinementPrompt(actor, SchemaContext(), queries, Feedback)
                + $"\n\n## Locked Queries (Cannot Be Modified)\nThe following query IDs are locked and should NOT be refined: {LockedQueryId}",
            LlmOperations.ActorPlan);
    }

    [Test]
    public async Task GeneratePlanAsync_WithoutAnInstruction_AsksTheModelWithTheSetupPrompt()
    {
        var actor = SeedActor();
        var requests = new List<LlmRequest>();
        var service = BuildService(PlanJson, llmRequests: requests);

        await service.GeneratePlanAsync(new GeneratePlanOptions { ActorId = ActorId }, CancellationToken.None);

        AssertSingleRequest(
            requests,
            AiActorPrompts.InitialSetupSystemPrompt,
            AiActorPrompts.BuildInitialSetupPrompt(actor, SchemaContext()),
            LlmOperations.ActorPlan);
    }

    // The execution row's phase while the model is asked, and how many saves came before it: a cycle that hangs in the
    // call leaves that trail.
    [TestCase(CycleKind.Think, AiActorExecutionPhase.Planning, 2)]
    [TestCase(CycleKind.Refine, AiActorExecutionPhase.Analyzing, 1)]
    [TestCase(CycleKind.Setup, AiActorExecutionPhase.Planning, 3)]
    public async Task TheModelCall_ComesAfterTheCyclesCheckpoint(CycleKind kind, AiActorExecutionPhase phase, int savesBefore)
    {
        AiActorExecutionPhase? phaseAtCall = null;
        var savesAtCall = -1;
        var service = BuildService(
            PlanJson,
            onLlmCall: () =>
            {
                phaseAtCall = _store.Executions.Single().Phase;
                savesAtCall = _store.SaveTokensCancelled.Count;
            });

        await RunAsync(kind, service, requiresApproval: true);

        phaseAtCall.Should().Be(phase);
        savesAtCall.Should().Be(savesBefore);
    }

    // The saves a cycle that proposes a plan makes from start to end: its checkpoints, its completion and the plan's
    // admission with its submission event.
    [TestCase(CycleKind.Think, 5)]
    [TestCase(CycleKind.Refine, 3)]
    [TestCase(CycleKind.Setup, 6)]
    public async Task AProposingCycle_SavesAtItsCheckpointsOnly(CycleKind kind, int saves)
    {
        var service = BuildService(PlanJson);

        await RunAsync(kind, service, requiresApproval: true);

        SingleCompletedExecution();
        _store.SaveTokensCancelled.Should().HaveCount(saves);
        _audit.Of(SecurityAuditActions.AiPlanSubmitted).Should().ContainSingle();
    }

    [TestCase(CycleKind.Think)]
    [TestCase(CycleKind.Refine)]
    [TestCase(CycleKind.Setup)]
    public async Task AReplyWithoutJson_CompletesTheCycleWithTheReplyAsItsSummary(CycleKind kind)
    {
        var service = BuildService(NotJsonReply);

        var (result, actor) = await RunAsync(kind, service, requiresApproval: true);

        var execution = SingleCompletedExecution();
        execution.DecisionSummary.Should().Be(NotJsonReply);
        StoredActions(execution).Should().BeEmpty();
        execution.TokensUsed.Should().Be(30);
        execution.Model.Should().Be("stub");
        actor.Status.Should().Be(AiActorStatus.Active);
        _store.Plans.Should().BeEmpty();
        _audit.Of(SecurityAuditActions.AiPlanSubmitted).Should().BeEmpty();
        if (result != null)
        {
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Findings.Should().BeEmpty();
        }
    }

    [TestCase(CycleKind.Think)]
    [TestCase(CycleKind.Refine)]
    [TestCase(CycleKind.Setup)]
    public async Task AReplyThatIsNotValidJson_CompletesTheCycleNotingTheParseFailure(CycleKind kind)
    {
        var service = BuildService(InvalidJsonReply);

        var (result, actor) = await RunAsync(kind, service, requiresApproval: true);

        var execution = SingleCompletedExecution();
        execution.DecisionSummary.Should().StartWith("Failed to parse response: ");
        StoredActions(execution).Should().BeEmpty();
        actor.Status.Should().Be(AiActorStatus.Active);
        actor.LastError.Should().BeNull();
        _store.Plans.Should().BeEmpty();
        if (result != null)
        {
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.Findings.Should().Equal("LLM response was not valid JSON");
        }
    }

    [TestCase(NotJsonReply)]
    [TestCase(InvalidJsonReply)]
    public async Task GeneratePlanAsync_WithAReplyItCannotRead_CreatesNoPlan(string reply)
    {
        SeedActor();
        var service = BuildService(reply);

        var result = await service.GeneratePlanAsync(new GeneratePlanOptions { ActorId = ActorId }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(AiActorService.NoActionsProposedMessage);
        _store.Plans.Should().BeEmpty();
        _audit.Events.Should().BeEmpty();
    }

    [Test]
    public async Task GeneratePlanAsync_RecordsTheCompletionsUsageOnThePlan()
    {
        SeedActor();
        var service = BuildService(PlanJson);

        var result = await service.GeneratePlanAsync(new GeneratePlanOptions { ActorId = ActorId }, CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.TokensUsed.Should().Be(30);
        result.EstimatedCost.Should().Be(0.01m);
        _store.Plans.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { TokensUsed = 30, EstimatedCost = 0.01m, Model = "stub", Analysis = "one stale monitor, one gap" },
            x => x.ExcludingMissingMembers());
        _store.Executions.Should().BeEmpty();
    }

    [Test]
    public async Task RefineActorAsync_StoresTheModelsReplyAsTheAssistantTurn()
    {
        SeedActor();
        var service = BuildService(PlanJson);

        await service.RefineActorAsync(ActorId, Feedback, CancellationToken.None);

        var execution = SingleCompletedExecution();
        _store.Conversations.Should().BeEquivalentTo(
            new[]
            {
                new { Role = ConversationRole.User, MessageContent = Feedback, TurnNumber = 1, AiActorExecutionId = (int?)null },
                new { Role = ConversationRole.Assistant, MessageContent = PlanJson, TurnNumber = 2, AiActorExecutionId = (int?)execution.Id }
            },
            x => x.ExcludingMissingMembers().WithStrictOrdering());
        _store.Conversations[1].TokensUsed.Should().Be(30);
        _store.Conversations[1].Model.Should().Be("stub");
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_WhenTheModelAsksToNotify_RecordsTheIntent()
    {
        SeedActor();
        var service = BuildService(NotifyJson);

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.NotificationsTriggered.Should().Be(1);
        result.Findings.Should().Equal("failures doubled");
        SingleCompletedExecution().NotificationsTriggered.Should().Be(1);
    }

    [TestCase(CycleKind.Refine)]
    [TestCase(CycleKind.Setup)]
    public async Task OtherCycles_NeverRecordANotification(CycleKind kind)
    {
        var service = BuildService(NotifyJson);

        await RunAsync(kind, service, requiresApproval: true);

        SingleCompletedExecution().NotificationsTriggered.Should().Be(0);
    }

    [TestCase(CycleKind.Think, 1)]
    [TestCase(CycleKind.Refine, 0)]
    [TestCase(CycleKind.Setup, 1)]
    public async Task NoApprovalRequired_AnExecutedCreation_IsCountedWithTheCompletionsUsage(CycleKind kind, int thinkCount)
    {
        _queryService
            .Setup(x => x.PreviewQuery(0, It.IsAny<QueryDraft>(), It.IsAny<ListRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryPreviewResult { Success = true });
        var service = BuildService(CreateOnlyJson);

        var (result, actor) = await RunAsync(kind, service, requiresApproval: false);

        var execution = SingleCompletedExecution();
        execution.Should().BeEquivalentTo(
            new { QueriesCreated = 1, QueriesRefined = 0, SubscriptionsCreated = 0, TokensUsed = 30, EstimatedCost = 0.01m, Model = "stub", DecisionSummary = "one gap" },
            x => x.ExcludingMissingMembers());
        StoredActions(execution).Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { ActionType = AiActorActionType.CreateQuery, Success = true, Proposed = false, QueryName = "Failed payments" },
            x => x.ExcludingMissingMembers());
        actor.TotalTokensUsed.Should().Be(30);
        actor.TotalCost.Should().Be(0.01m);
        actor.ThinkCount.Should().Be(thinkCount);
        _store.Plans.Should().BeEmpty();
        if (result != null)
        {
            result.QueriesCreated.Should().Be(1);
            result.TokensUsed.Should().Be(30);
            result.Actions.Should().ContainSingle().Which.Success.Should().BeTrue();
        }
    }

    [TestCase(CycleKind.Think)]
    [TestCase(CycleKind.Refine)]
    [TestCase(CycleKind.Setup)]
    public async Task NoApprovalRequired_AnotherActorsQuery_IsRefusedByTheOwnershipGuard(CycleKind kind)
    {
        _store.Queries.Add(new Query { Id = OtherActorsQueryId, Name = "Someone else's monitor", AiActorId = 99 });
        var service = BuildService(OtherActorsArchiveJson);

        await RunAsync(kind, service, requiresApproval: false);

        var execution = SingleCompletedExecution();
        StoredActions(execution).Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { ActionType = AiActorActionType.ArchiveQuery, Success = false, Proposed = false, ErrorMessage = $"Query {OtherActorsQueryId}: Not owned by this actor" },
            x => x.ExcludingMissingMembers());
        _queryService.VerifyNoOtherCalls();
        _store.Plans.Should().BeEmpty();
        _store.Queries.Single(x => x.Id == OtherActorsQueryId).ArchivedTime.Should().BeNull();
    }

    private async Task<(AiActorThinkResult? Result, ActorEntity Actor)> RunAsync(CycleKind kind, AiActorService service, bool requiresApproval)
    {
        if (kind == CycleKind.Setup)
        {
            return (null, await CreateActorAsync(service, requiresApproval));
        }

        var actor = SeedActor();
        actor.RequiresApproval = requiresApproval;
        var result = kind == CycleKind.Think
            ? await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None)
            : await service.RefineActorAsync(ActorId, Feedback, CancellationToken.None);

        return (result, actor);
    }

    private static Task<ActorEntity> CreateActorAsync(AiActorService service, bool requiresApproval)
    {
        return service.CreateActorAsync(
            new CreateAiActorOptions
            {
                Name = "Payments watcher",
                Instructions = "Watch failed payments",
                DataSourceId = DataSourceId,
                ActivateImmediately = true,
                RequiresApproval = requiresApproval
            },
            CancellationToken.None);
    }

    private static string SchemaContext() => AiActorPrompts.FormatSchemaContext([]);

    private static AiActorPrompts.QueryContext OwnedQueryContext(string sql = "") =>
        new() { QueryId = OwnedQueryId, QueryName = "Stale monitor", Sql = sql };

    private static void AssertSingleRequest(List<LlmRequest> requests, string systemPrompt, string userPrompt, string operation)
    {
        var request = requests.Should().ContainSingle().Subject;
        request.SystemPrompt.Should().Be(systemPrompt);
        request.Messages.Should().ContainSingle().Which.Should().Be(new ChatMessage(ConversationRole.User, userPrompt));
        request.Should().BeEquivalentTo(
            new { Temperature = 0.3m, MaxTokens = 4096, Priority = LlmRequestPriority.Interactive, Operation = operation, Tier = LlmTier.Default },
            x => x.ExcludingMissingMembers());
    }
}

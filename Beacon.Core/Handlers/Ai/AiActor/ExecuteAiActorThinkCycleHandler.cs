using MediatR;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;
using Beacon.Core.Data.Entities;



namespace Beacon.Core.Handlers.Ai.AiActor;

public record ExecuteAiActorThinkCycleCommand : IRequest<ExecuteAiActorThinkCycleResult>
{
    public required int ActorId { get; init; }
}

public record ExecuteAiActorThinkCycleResult
{
    public bool Success { get; init; }
    public int ExecutionId { get; init; }
    public AiActorExecutionPhase Phase { get; init; }
    public string? DecisionSummary { get; init; }
    public List<string> Findings { get; init; } = new();
    public int QueriesAnalyzed { get; init; }
    public int QueriesCreated { get; init; }
    public int QueriesRefined { get; init; }
    public int SubscriptionsCreated { get; init; }
    public int NotificationsTriggered { get; init; }
    public int TokensUsed { get; init; }
    public decimal EstimatedCost { get; init; }
    public TimeSpan Duration { get; init; }
    public string? ErrorMessage { get; init; }
    public List<ExecutedActionInfo> Actions { get; init; } = new();
}

public record ExecutedActionInfo
{
    public AiActorActionType ActionType { get; init; }
    public string? Reasoning { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public int? ResultEntityId { get; init; }
}

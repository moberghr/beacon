using MediatR;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Ai;
using Semantico.Core.Data.Entities;



namespace Semantico.Core.Handlers.Ai.AiActor;

public record GetAiActorExecutionHistoryQuery : IRequest<GetAiActorExecutionHistoryResult>
{
    public required int ActorId { get; init; }
    public int? Limit { get; init; }
}

public record GetAiActorExecutionHistoryResult
{
    public int ActorId { get; init; }
    public List<ExecutionHistoryItem> Executions { get; init; } = new();
}

public record ExecutionHistoryItem
{
    public int ExecutionId { get; init; }
    public int? TriggeringSubscriptionId { get; init; }
    public AiActorExecutionPhase Phase { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public int QueriesAnalyzed { get; init; }
    public int QueriesCreated { get; init; }
    public int QueriesRefined { get; init; }
    public int SubscriptionsCreated { get; init; }
    public int NotificationsTriggered { get; init; }
    public int TokensUsed { get; init; }
    public decimal EstimatedCost { get; init; }
    public string? Model { get; init; }
    public string? DecisionSummary { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ActionsJson { get; init; }
    public TimeSpan? Duration => CompletedAt.HasValue ? CompletedAt.Value - StartedAt : null;
}

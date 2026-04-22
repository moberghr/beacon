using MediatR;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;
using Beacon.Core.Data.Entities;



namespace Beacon.Core.Handlers.Ai.AiActor;

public record RefineAiActorCommand : IRequest<RefineAiActorResult>
{
    public required int ActorId { get; init; }
    public required string Feedback { get; init; }
}

public record RefineAiActorResult
{
    public bool Success { get; init; }
    public int ExecutionId { get; init; }
    public AiActorExecutionPhase Phase { get; init; }
    public string? DecisionSummary { get; init; }
    public int QueriesCreated { get; init; }
    public int QueriesRefined { get; init; }
    public int SubscriptionsCreated { get; init; }
    public int TokensUsed { get; init; }
    public decimal EstimatedCost { get; init; }
    public string? ErrorMessage { get; init; }
}

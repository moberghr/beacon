using MediatR;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Ai;
using Semantico.Core.Data.Entities;



namespace Semantico.Core.Handlers.Ai.AiActor;

public record ApproveAiActorPlanCommand : IRequest<ApproveAiActorPlanResult>
{
    public required int PlanId { get; init; }
    public string? UserId { get; init; }
    public string? Comment { get; init; }
}

public record ApproveAiActorPlanResult
{
    public bool Success { get; init; }
    public int? ExecutionId { get; init; }
    public string? DecisionSummary { get; init; }
    public int QueriesCreated { get; init; }
    public int QueriesRefined { get; init; }
    public int SubscriptionsCreated { get; init; }
    public int TokensUsed { get; init; }
    public decimal EstimatedCost { get; init; }
    public TimeSpan? Duration { get; init; }
    public string? ErrorMessage { get; init; }
}

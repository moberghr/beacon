using MediatR;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Ai;
using Semantico.Core.Data.Entities;



namespace Semantico.Core.Handlers.Ai.AiActor;

public record GenerateAiActorPlanCommand : IRequest<GenerateAiActorPlanResult>
{
    public required int ActorId { get; init; }
    public string? UserInstruction { get; init; }
    public int? TriggeringSubscriptionId { get; init; }
}

public record GenerateAiActorPlanResult
{
    public bool Success { get; init; }
    public int? PlanId { get; init; }
    public string? Analysis { get; init; }
    public List<string> Findings { get; init; } = [];
    public List<ProposedAction> ProposedActions { get; init; } = [];
    public int TokensUsed { get; init; }
    public decimal EstimatedCost { get; init; }
    public string? ErrorMessage { get; init; }
}

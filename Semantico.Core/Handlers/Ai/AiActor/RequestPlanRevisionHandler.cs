using MediatR;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Ai;
using Semantico.Core.Data.Entities;



namespace Semantico.Core.Handlers.Ai.AiActor;

public record RequestPlanRevisionCommand : IRequest<RequestPlanRevisionResult>
{
    public required int PlanId { get; init; }
    public string? UserId { get; init; }
    public required string Feedback { get; init; }
}

public record RequestPlanRevisionResult
{
    public bool Success { get; init; }
    public int OriginalPlanId { get; init; }
    public int? NewPlanId { get; init; }
    public string? Analysis { get; init; }
    public List<string> Findings { get; init; } = [];
    public List<ProposedAction> ProposedActions { get; init; } = [];
    public int TokensUsed { get; init; }
    public decimal EstimatedCost { get; init; }
    public string? ErrorMessage { get; init; }
}

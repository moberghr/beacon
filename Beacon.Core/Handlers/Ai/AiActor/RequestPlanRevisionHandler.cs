using MediatR;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;
using Beacon.Core.Data.Entities;



namespace Beacon.Core.Handlers.Ai.AiActor;

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

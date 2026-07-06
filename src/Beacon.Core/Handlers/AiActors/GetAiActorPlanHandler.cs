using MediatR;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;
using Beacon.Core.Data.Entities;



namespace Beacon.Core.Handlers.AiActors;

public record GetAiActorPlanQuery : IRequest<GetAiActorPlanResult?>
{
    public required int PlanId { get; init; }
}

public record GetAiActorPlanResult
{
    public int PlanId { get; init; }
    public int ActorId { get; init; }
    public string ActorName { get; init; } = null!;
    public AiActorPlanStatus Status { get; init; }
    public string? UserInstruction { get; init; }
    public string Analysis { get; init; } = null!;
    public List<string> Findings { get; init; } = [];
    public List<ProposedAction> ProposedActions { get; init; } = [];
    public DateTime ProposedAt { get; init; }
    public DateTime? ReviewedAt { get; init; }
    public string? ReviewedByUserId { get; init; }
    public string? ReviewerComment { get; init; }
    public DateTime? ExecutedAt { get; init; }
    public int? ExecutionId { get; init; }
    public int TokensUsed { get; init; }
    public decimal EstimatedCost { get; init; }
    public string? Model { get; init; }
    public int Version { get; init; }
    public int? ParentPlanId { get; init; }
}

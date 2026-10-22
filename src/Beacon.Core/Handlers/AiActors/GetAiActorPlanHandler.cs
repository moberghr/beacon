using MediatR;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;
using Beacon.Core.Data.Entities;
using Beacon.Core.Authorization;



namespace Beacon.Core.Handlers.AiActors;

public record GetAiActorPlanQuery : IRequest<GetAiActorPlanResult?>, IProjectAuthorizedRequest
{
    public required int PlanId { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.AiActorPlan(PlanId), ProjectAction.Read)
    ];
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

    /// <summary>When the plan expires (<c>Beacon:AiActors:PlanExpiryDays</c> after it was proposed) unless decided first.</summary>
    public DateTime ExpiresAt { get; init; }

    /// <summary>The plan was not decided before it expired: it can no longer be approved, rejected or revised.</summary>
    public bool IsExpired { get; init; }

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

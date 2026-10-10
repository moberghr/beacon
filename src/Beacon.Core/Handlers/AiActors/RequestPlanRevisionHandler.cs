using MediatR;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;
using Beacon.Core.Data.Entities;
using Beacon.Core.Authorization;



namespace Beacon.Core.Handlers.AiActors;

/// <summary>Asks the AI actor to revise a pending plan with feedback. Allowed for the actor's creator or an Admin.</summary>
public record RequestPlanRevisionCommand : IRequest<RequestPlanRevisionResult>, IProjectAuthorizedRequest
{
    public required int PlanId { get; init; }
    [ProjectScopeExempt("the caller, set by the endpoint; not a project resource")]
    public string? UserId { get; init; }
    public required string Feedback { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.AiActorPlan(PlanId), ProjectAction.Write)
    ];
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

using MediatR;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;
using Beacon.Core.Data.Entities;
using Beacon.Core.Authorization;



namespace Beacon.Core.Handlers.AiActors;

public record RejectAiActorPlanCommand : IRequest<RejectAiActorPlanResult>, IProjectAuthorizedRequest
{
    public required int PlanId { get; init; }
    [ProjectScopeExempt("the caller, set by the endpoint; not a project resource")]
    public string? UserId { get; init; }
    public required string Reason { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.AiActorPlan(PlanId), ProjectAction.Write)
    ];
}

public record RejectAiActorPlanResult
{
    public bool Success { get; init; }
    public int PlanId { get; init; }
}

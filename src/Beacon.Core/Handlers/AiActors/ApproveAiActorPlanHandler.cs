using MediatR;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;
using Beacon.Core.Data.Entities;
using Beacon.Core.Authorization;



namespace Beacon.Core.Handlers.AiActors;

public record ApproveAiActorPlanCommand : IRequest<ApproveAiActorPlanResult>, IProjectAuthorizedRequest
{
    public required int PlanId { get; init; }
    [ProjectScopeExempt("the caller, set by the endpoint; not a project resource")]
    public string? UserId { get; init; }
    public string? Comment { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.AiActorPlan(PlanId), ProjectAction.Write)
    ];
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

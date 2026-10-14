using MediatR;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;
using Beacon.Core.Data.Entities;
using Beacon.Core.Authorization;



namespace Beacon.Core.Handlers.AiActors;

public record GetPendingPlansQuery : IRequest<GetPendingPlansResult>, IProjectAuthorizedRequest
{
    public required int ActorId { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.AiActor(ActorId), ProjectAction.Read)
    ];
}

public record GetPendingPlansResult
{
    public List<PendingPlanSummary> Plans { get; init; } = [];
}

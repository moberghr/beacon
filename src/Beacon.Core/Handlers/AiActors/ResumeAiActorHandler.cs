using MediatR;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;
using Beacon.Core.Data.Entities;
using Beacon.Core.Authorization;



namespace Beacon.Core.Handlers.AiActors;

public record ResumeAiActorCommand : IRequest<ResumeAiActorResult>, IProjectAuthorizedRequest
{
    public required int ActorId { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.AiActor(ActorId), ProjectAction.Write)
    ];
}

public record ResumeAiActorResult
{
    public bool Success { get; init; }
    public int ActorId { get; init; }
}

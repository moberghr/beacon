using Beacon.Core.Authorization;
using Beacon.Core.BackgroundTasks;
using Beacon.Core.Data.Enums;
using MediatR;

namespace Beacon.Core.Handlers.AiActors;

internal sealed class GetAiActorThinkTaskHandler(IBackgroundTaskService backgroundTasks)
    : IRequestHandler<GetAiActorThinkTaskQuery, BackgroundTaskStatus?>
{
    public Task<BackgroundTaskStatus?> Handle(GetAiActorThinkTaskQuery request, CancellationToken cancellationToken)
        => backgroundTasks.GetStatusAsync(BackgroundTaskKind.AiActorThink, request.ActorId, request.TaskId, cancellationToken);
}

/// <summary>
/// The state of an on-demand think cycle of the AI actor (<see cref="StartAiActorThinkCommand"/>), or null when the actor
/// has no think task with that id. Read with the check that starts one.
/// </summary>
public record GetAiActorThinkTaskQuery(
    int ActorId,
    [property: ProjectScopeExempt("read only together with its actor: a task of another actor is not found")] int TaskId)
    : IRequest<BackgroundTaskStatus?>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.AiActor(ActorId), ProjectAction.Query)
    ];
}

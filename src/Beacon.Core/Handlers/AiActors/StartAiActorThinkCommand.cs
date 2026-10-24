using Beacon.Core.Authorization;
using Beacon.Core.BackgroundTasks;
using MediatR;

namespace Beacon.Core.Handlers.AiActors;

/// <summary>
/// Starts a think cycle of the AI actor as a background task and answers at once with its ticket; its state is read with
/// <see cref="GetAiActorThinkTaskQuery"/>, and the user who started it gets <c>JobStatusChanged</c> for its job. Only the
/// actor's creator or an Admin may start it (the same rule as <see cref="ExecuteAiActorThinkCycleCommand"/>, which runs
/// the cycle in the request and stays for 5.0 callers). The task runs as the caller, re-checks that they still may run
/// the actor and reach its data source right before the LLM call, and is skipped (never run as the system) when not.
/// A second start while one is queued or running answers 429.
/// </summary>
public record StartAiActorThinkCommand : IRequest<BackgroundTaskTicket>, IProjectAuthorizedRequest
{
    public required int ActorId { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.AiActor(ActorId), ProjectAction.Query)
    ];
}

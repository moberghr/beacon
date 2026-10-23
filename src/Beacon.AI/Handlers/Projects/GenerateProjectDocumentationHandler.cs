using MediatR;
using Beacon.Core.Authorization;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Worker;

namespace Beacon.AI.Handlers.Projects;

internal sealed class GenerateProjectDocumentationHandler(
    IBeaconScheduler beaconScheduler,
    IBeaconActorAccessor actorAccessor,
    IActorUserResolver actorUserResolver)
    : IRequestHandler<GenerateProjectDocumentationCommand, GenerateProjectDocumentationResult>
{
    public async Task<GenerateProjectDocumentationResult> Handle(
        GenerateProjectDocumentationCommand request,
        CancellationToken cancellationToken)
    {
        // The job records who enqueued it: the caller's stored user when the request names none. notifyUserId lets the
        // host scope JobStatusChanged push events on /beacon/api/hub to the enqueueing user only: the actor's UserId, the
        // id the hub addresses the caller's connection by (HubUserIdProvider).
        var enqueuedBy = request.UserId != 0
            ? request.UserId
            : await actorUserResolver.ResolveActorUserIdAsync(cancellationToken) ?? 0;
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        var jobId = await beaconScheduler.EnqueueProjectDocumentation(
            request.ProjectId,
            enqueuedBy,
            string.IsNullOrWhiteSpace(actor.UserId) ? null : actor.UserId);

        return new GenerateProjectDocumentationResult(jobId);
    }
}

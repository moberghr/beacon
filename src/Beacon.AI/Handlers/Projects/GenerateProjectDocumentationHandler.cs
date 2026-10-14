using MediatR;
using Beacon.Core.Authorization;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Worker;

namespace Beacon.AI.Handlers.Projects;

internal sealed class GenerateProjectDocumentationHandler(
    IBeaconScheduler beaconScheduler,
    IBeaconUserContext userContext,
    IActorUserResolver actorUserResolver)
    : IRequestHandler<GenerateProjectDocumentationCommand, GenerateProjectDocumentationResult>
{
    public async Task<GenerateProjectDocumentationResult> Handle(
        GenerateProjectDocumentationCommand request,
        CancellationToken cancellationToken)
    {
        // The job records who enqueued it: the caller's stored user when the request names none. notifyUserId lets the
        // host scope JobStatusChanged push events on /beacon/api/hub to the enqueueing user only.
        var enqueuedBy = request.UserId != 0
            ? request.UserId
            : await actorUserResolver.ResolveActorUserIdAsync(cancellationToken) ?? 0;
        var jobId = await beaconScheduler.EnqueueProjectDocumentation(
            request.ProjectId,
            enqueuedBy,
            string.IsNullOrWhiteSpace(userContext.UserId) ? null : userContext.UserId);

        return new GenerateProjectDocumentationResult(jobId);
    }
}

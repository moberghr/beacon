using MediatR;
using Beacon.Core.Authorization;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Worker;

namespace Beacon.AI.Handlers.Projects;

internal sealed class GenerateProjectDocumentationHandler(
    IBeaconScheduler beaconScheduler,
    IBeaconUserContext userContext)
    : IRequestHandler<GenerateProjectDocumentationCommand, GenerateProjectDocumentationResult>
{
    public async Task<GenerateProjectDocumentationResult> Handle(
        GenerateProjectDocumentationCommand request,
        CancellationToken cancellationToken)
    {
        // notifyUserId lets the host scope JobStatusChanged push events on
        // /beacon/api/hub to the enqueueing user only.
        var jobId = await beaconScheduler.EnqueueProjectDocumentation(
            request.ProjectId,
            request.UserId,
            string.IsNullOrWhiteSpace(userContext.UserId) ? null : userContext.UserId);

        return new GenerateProjectDocumentationResult(jobId);
    }
}

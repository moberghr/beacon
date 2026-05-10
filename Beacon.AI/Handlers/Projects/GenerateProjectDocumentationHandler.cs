using Hangfire;
using MediatR;
using Beacon.AI.Services.Documentation;
using Beacon.Core.Authorization;
using Beacon.Core.Handlers.Projects;

namespace Beacon.AI.Handlers.Projects;

internal sealed class GenerateProjectDocumentationHandler(
    IBackgroundJobClient backgroundJobClient,
    IBeaconUserContext userContext)
    : IRequestHandler<GenerateProjectDocumentationCommand, GenerateProjectDocumentationResult>
{
    public Task<GenerateProjectDocumentationResult> Handle(
        GenerateProjectDocumentationCommand request,
        CancellationToken cancellationToken)
    {
        var jobId = backgroundJobClient.Enqueue<IProjectDocumentationService>(
            svc => svc.GenerateDocumentationAsync(request.ProjectId, request.UserId, CancellationToken.None));

        // Tag the job with the enqueueing user so HangfireSignalRJobFilter publishes
        // JobStatusChanged events to /beacon/api/hub for that user only.
        if (!string.IsNullOrWhiteSpace(userContext.UserId))
        {
            JobStorage.Current.GetConnection().SetJobParameter(jobId, "BeaconUserId", userContext.UserId);
        }

        return Task.FromResult(new GenerateProjectDocumentationResult(jobId));
    }
}

using Hangfire;
using MediatR;
using Beacon.AI.Services.Documentation;
using Beacon.Core.Handlers.Projects;

namespace Beacon.AI.Handlers.Projects;

internal sealed class GenerateProjectDocumentationHandler(
    IBackgroundJobClient backgroundJobClient) : IRequestHandler<GenerateProjectDocumentationCommand, GenerateProjectDocumentationResult>
{
    public Task<GenerateProjectDocumentationResult> Handle(
        GenerateProjectDocumentationCommand request,
        CancellationToken cancellationToken)
    {
        var jobId = backgroundJobClient.Enqueue<IProjectDocumentationService>(
            svc => svc.GenerateDocumentationAsync(request.ProjectId, request.UserId, CancellationToken.None));

        return Task.FromResult(new GenerateProjectDocumentationResult(jobId));
    }
}

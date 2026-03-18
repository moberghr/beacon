using Hangfire;
using MediatR;
using Semantico.AI.Services.Documentation;
using Semantico.Core.Handlers.Projects;

namespace Semantico.AI.Handlers.Projects;

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

using MediatR;
using Beacon.Core.BackgroundTasks;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Models;

namespace Beacon.AI.Handlers.Projects;

/// <summary>
/// The 5.0 route's command: starts the same background task as <see cref="StartProjectDocumentationGenerationCommand"/>, so
/// the generation runs as the caller (the command's user id is no longer used) and answers with the job id.
/// </summary>
internal sealed class GenerateProjectDocumentationHandler(IBackgroundTaskService backgroundTasks)
    : IRequestHandler<GenerateProjectDocumentationCommand, GenerateProjectDocumentationResult>
{
    public async Task<GenerateProjectDocumentationResult> Handle(
        GenerateProjectDocumentationCommand request,
        CancellationToken cancellationToken)
    {
        var ticket = await backgroundTasks.StartAsync(
                BackgroundTaskKind.DocumentationGeneration,
                request.ProjectId,
                null,
                cancellationToken)
            ?? throw new BeaconRateLimitedException("The documentation of this project is already being generated.");

        return new GenerateProjectDocumentationResult(ticket.JobId ?? string.Empty);
    }
}

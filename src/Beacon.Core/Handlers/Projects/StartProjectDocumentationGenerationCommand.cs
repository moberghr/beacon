using Beacon.Core.Authorization;
using Beacon.Core.BackgroundTasks;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.SecurityAudit;
using MediatR;

namespace Beacon.Core.Handlers.Projects;

internal sealed class StartProjectDocumentationGenerationHandler(IBackgroundTaskService backgroundTasks)
    : IRequestHandler<StartProjectDocumentationGenerationCommand, BackgroundTaskTicket>
{
    public async Task<BackgroundTaskTicket> Handle(StartProjectDocumentationGenerationCommand request, CancellationToken cancellationToken)
    {
        if (request.ProjectId <= 0)
        {
            throw new InvalidOperationException("Project id must be positive.");
        }

        return await backgroundTasks.StartAsync(
                BackgroundTaskKind.DocumentationGeneration,
                request.ProjectId,
                taskId => DocumentationGenerationAudit.Started(request.ProjectId, taskId),
                cancellationToken)
            ?? throw new BeaconRateLimitedException("The documentation of this project is already being generated.");
    }
}

/// <summary>The security event of an accepted documentation generation: ids only, recorded in the save that records the task.</summary>
public static class DocumentationGenerationAudit
{
    public static SecurityAuditEntry Started(int projectId, int taskId)
        => new SecurityAuditEntry(SecurityAuditActions.DocumentationGenerationStarted)
            .On(SecurityAuditResources.Project, projectId)
            .InProject(projectId)
            .With(new SecurityAuditDetails().Add("taskId", taskId));
}

/// <summary>
/// Starts the regeneration of a project's documentation as a background task and answers at once with its ticket; its state
/// is read with <see cref="GetProjectDocumentationGenerationQuery"/>, and the user who started it gets
/// <c>JobStatusChanged</c> for its job. Needs <see cref="ProjectAction.Curate"/> in the project, like editing a section. The
/// task runs as the caller and is skipped when, by then, the caller may no longer curate the project. A generation of the
/// project that is queued or running refuses a second one (429).
/// </summary>
public record StartProjectDocumentationGenerationCommand(int ProjectId) : IRequest<BackgroundTaskTicket>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Project(ProjectId), ProjectAction.Curate)
    ];
}

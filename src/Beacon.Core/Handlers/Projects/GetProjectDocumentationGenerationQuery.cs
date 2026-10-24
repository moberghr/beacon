using Beacon.Core.Authorization;
using Beacon.Core.BackgroundTasks;
using Beacon.Core.Data.Enums;
using MediatR;

namespace Beacon.Core.Handlers.Projects;

internal sealed class GetProjectDocumentationGenerationHandler(IBackgroundTaskService backgroundTasks)
    : IRequestHandler<GetProjectDocumentationGenerationQuery, BackgroundTaskStatus?>
{
    public Task<BackgroundTaskStatus?> Handle(GetProjectDocumentationGenerationQuery request, CancellationToken cancellationToken)
        => backgroundTasks.GetStatusAsync(BackgroundTaskKind.DocumentationGeneration, request.ProjectId, request.TaskId, cancellationToken);
}

/// <summary>
/// The state of a background documentation generation of the project
/// (<see cref="StartProjectDocumentationGenerationCommand"/>), or null when the project has no generation task with that id.
/// </summary>
public record GetProjectDocumentationGenerationQuery(
    int ProjectId,
    [property: ProjectScopeExempt("read only together with its project: a task of another project is not found")] int TaskId)
    : IRequest<BackgroundTaskStatus?>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Project(ProjectId), ProjectAction.Read)
    ];
}

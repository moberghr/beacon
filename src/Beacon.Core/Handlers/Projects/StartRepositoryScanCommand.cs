using Beacon.Core.Authorization;
using Beacon.Core.BackgroundTasks;
using MediatR;

namespace Beacon.Core.Handlers.Projects;

/// <summary>
/// Starts a scan of every repository of the project as a background task and answers at once with its ticket; its state is
/// read with <see cref="GetRepositoryScanQuery"/>, each repository's own result on the project's repositories, and the user
/// who started it gets <c>JobStatusChanged</c> for its job. The same check as <see cref="ScanAllRepositoriesCommand"/>; a
/// scan of the project that is queued or running refuses a second one (409). The task runs as the caller and is skipped
/// when, by then, the caller may no longer change the project.
/// </summary>
public record StartRepositoryScanCommand(int ProjectId) : IRequest<BackgroundTaskTicket>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Project(ProjectId), ProjectAction.Write)
    ];
}

/// <summary>
/// The state of a background repository scan of the project (<see cref="StartRepositoryScanCommand"/>), or null when the
/// project has no scan task with that id; its warning count is the repositories whose scan failed or hit a limit. Read with
/// the check that starts one.
/// </summary>
public record GetRepositoryScanQuery(
    int ProjectId,
    [property: ProjectScopeExempt("read only together with its project: a task of another project is not found")] int TaskId)
    : IRequest<BackgroundTaskStatus?>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Project(ProjectId), ProjectAction.Write)
    ];
}

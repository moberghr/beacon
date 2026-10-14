using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Projects;

public record ScanAllRepositoriesCommand(int ProjectId) : IRequest<ScanAllRepositoriesResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Project(ProjectId), ProjectAction.Write)
    ];
}

public record ScanAllRepositoriesResult(int ScannedCount, List<string> Errors);

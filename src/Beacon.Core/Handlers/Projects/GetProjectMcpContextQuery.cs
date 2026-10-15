using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Projects;

public record GetProjectMcpContextQuery(int ProjectId) : IRequest<GetProjectMcpContextResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Project(ProjectId), ProjectAction.Read)
    ];
}

public record GetProjectMcpContextResult(string Context);

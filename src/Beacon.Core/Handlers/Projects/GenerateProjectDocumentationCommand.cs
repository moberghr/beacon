using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Projects;

public record GenerateProjectDocumentationCommand(
    int ProjectId,
    [property: ProjectScopeExempt("the enqueuing user, recorded with the job; not a project resource")] int UserId) : IRequest<GenerateProjectDocumentationResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Project(ProjectId), ProjectAction.Write)
    ];
}

public record GenerateProjectDocumentationResult(string JobId);

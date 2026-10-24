using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Projects;

/// <summary>
/// Regenerates a project's documentation in the background (kept for 5.0 callers; the UI uses
/// <see cref="StartProjectDocumentationGenerationCommand"/>, whose state it can read). The documentation replaces what the LLM is given as the
/// project's documentation, so it needs <see cref="ProjectAction.Curate"/> in the project, as editing a section does.
/// </summary>
public record GenerateProjectDocumentationCommand(
    int ProjectId,
    [property: ProjectScopeExempt("the enqueuing user, recorded with the job; not a project resource")] int UserId) : IRequest<GenerateProjectDocumentationResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Project(ProjectId), ProjectAction.Curate)
    ];
}

public record GenerateProjectDocumentationResult(string JobId);

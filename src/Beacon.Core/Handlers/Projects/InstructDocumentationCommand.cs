using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Projects;

public record InstructDocumentationCommand(
    int SectionId,
    string Instruction) : IRequest<InstructDocumentationResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.DocumentationSection(SectionId), ProjectAction.Query)
    ];
}

public record InstructDocumentationResult(string UpdatedContent);

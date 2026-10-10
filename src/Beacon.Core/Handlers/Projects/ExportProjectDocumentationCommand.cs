using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Projects;

public record ExportProjectDocumentationCommand(int DocumentationId, bool AsHtml = false) : IRequest<ExportProjectDocumentationResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Documentation(DocumentationId), ProjectAction.Read)
    ];
}

public record ExportProjectDocumentationResult(string Content, string ContentType, string FileName);

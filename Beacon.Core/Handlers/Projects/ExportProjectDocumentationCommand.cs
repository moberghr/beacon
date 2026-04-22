using MediatR;

namespace Beacon.Core.Handlers.Projects;

public record ExportProjectDocumentationCommand(int DocumentationId, bool AsHtml = false) : IRequest<ExportProjectDocumentationResult>;

public record ExportProjectDocumentationResult(string Content, string ContentType, string FileName);

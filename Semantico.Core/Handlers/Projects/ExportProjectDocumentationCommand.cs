using MediatR;

namespace Semantico.Core.Handlers.Projects;

public record ExportProjectDocumentationCommand(int DocumentationId, bool AsHtml = false) : IRequest<ExportProjectDocumentationResult>;

public record ExportProjectDocumentationResult(string Content, string ContentType, string FileName);

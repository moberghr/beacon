using MediatR;

namespace Semantico.Core.Handlers.Ai.ExportDocumentation;

public record ExportDocumentationToMarkdownCommand : IRequest<string>
{
    public int DocumentationId { get; init; }
}

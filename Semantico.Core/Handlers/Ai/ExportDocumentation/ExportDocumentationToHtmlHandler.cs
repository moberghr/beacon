using MediatR;

namespace Semantico.Core.Handlers.Ai.ExportDocumentation;

public record ExportDocumentationToHtmlCommand : IRequest<string>
{
    public int DocumentationId { get; init; }
    public string? CustomCss { get; init; }
}

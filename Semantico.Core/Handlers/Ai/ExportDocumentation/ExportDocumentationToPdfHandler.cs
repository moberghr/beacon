using MediatR;

namespace Semantico.Core.Handlers.Ai.ExportDocumentation;

public record ExportDocumentationToPdfCommand : IRequest<byte[]>
{
    public int DocumentationId { get; init; }
}

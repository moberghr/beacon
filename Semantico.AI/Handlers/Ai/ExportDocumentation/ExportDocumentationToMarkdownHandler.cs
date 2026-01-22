using MediatR;
using Semantico.AI.Services.Ai;
using Semantico.Core.Handlers.Ai.ExportDocumentation;

namespace Semantico.AI.Handlers.Ai.ExportDocumentation;

internal sealed class ExportDocumentationToMarkdownHandler
    : IRequestHandler<ExportDocumentationToMarkdownCommand, string>
{
    private readonly IAiDocumentationService _documentationService;

    public ExportDocumentationToMarkdownHandler(IAiDocumentationService documentationService)
    {
        _documentationService = documentationService;
    }

    public async Task<string> Handle(
        ExportDocumentationToMarkdownCommand request,
        CancellationToken cancellationToken)
    {
        return await _documentationService.ExportToMarkdownAsync(
            request.DocumentationId,
            cancellationToken);
    }
}

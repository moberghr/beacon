using MediatR;
using Semantico.AI.Services.Ai;
using Semantico.Core.Handlers.Ai.ExportDocumentation;

namespace Semantico.AI.Handlers.Ai.ExportDocumentation;

internal sealed class ExportDocumentationToHtmlHandler
    : IRequestHandler<ExportDocumentationToHtmlCommand, string>
{
    private readonly IAiDocumentationService _documentationService;

    public ExportDocumentationToHtmlHandler(IAiDocumentationService documentationService)
    {
        _documentationService = documentationService;
    }

    public async Task<string> Handle(
        ExportDocumentationToHtmlCommand request,
        CancellationToken cancellationToken)
    {
        return await _documentationService.ExportToHtmlAsync(
            request.DocumentationId,
            request.CustomCss,
            cancellationToken);
    }
}

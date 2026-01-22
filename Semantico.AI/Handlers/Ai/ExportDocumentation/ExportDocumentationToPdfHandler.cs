using MediatR;
using Semantico.AI.Services.Ai;
using Semantico.Core.Handlers.Ai.ExportDocumentation;

namespace Semantico.AI.Handlers.Ai.ExportDocumentation;

internal sealed class ExportDocumentationToPdfHandler
    : IRequestHandler<ExportDocumentationToPdfCommand, byte[]>
{
    private readonly IAiDocumentationService _documentationService;

    public ExportDocumentationToPdfHandler(IAiDocumentationService documentationService)
    {
        _documentationService = documentationService;
    }

    public async Task<byte[]> Handle(
        ExportDocumentationToPdfCommand request,
        CancellationToken cancellationToken)
    {
        return await _documentationService.ExportToPdfAsync(
            request.DocumentationId,
            cancellationToken);
    }
}

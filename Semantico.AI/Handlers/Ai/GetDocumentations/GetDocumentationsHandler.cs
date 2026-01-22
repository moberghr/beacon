using MediatR;
using Semantico.AI.Services.Ai;
using Semantico.Core.Data.Entities;
using Semantico.Core.Handlers.Ai.GetDocumentations;

namespace Semantico.AI.Handlers.Ai.GetDocumentations;

internal sealed class GetDocumentationsHandler
    : IRequestHandler<GetDocumentationsCommand, List<DataSourceDocumentation>>
{
    private readonly IAiDocumentationService _documentationService;

    public GetDocumentationsHandler(IAiDocumentationService documentationService)
    {
        _documentationService = documentationService;
    }

    public async Task<List<DataSourceDocumentation>> Handle(
        GetDocumentationsCommand request,
        CancellationToken cancellationToken)
    {
        return await _documentationService.GetDocumentationsAsync(
            request.DataSourceId,
            cancellationToken);
    }
}

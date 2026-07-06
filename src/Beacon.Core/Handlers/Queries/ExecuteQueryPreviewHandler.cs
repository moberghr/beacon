using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Queries;

internal sealed class ExecuteQueryPreviewHandler(IQueryExecutionPreviewService previewService)
    : IRequestHandler<ExecuteQueryPreviewCommand, QueryExecutionResult>
{
    public async Task<QueryExecutionResult> Handle(ExecuteQueryPreviewCommand request, CancellationToken cancellationToken)
    {
        var result = await previewService.ExecuteQueryPreview(request.QueryId, cancellationToken);

        if (result == null)
        {
            throw new InvalidOperationException($"Query #{request.QueryId} preview failed.");
        }

        return result;
    }
}

public record ExecuteQueryPreviewCommand : IRequest<QueryExecutionResult>
{
    public required int QueryId { get; init; }
}

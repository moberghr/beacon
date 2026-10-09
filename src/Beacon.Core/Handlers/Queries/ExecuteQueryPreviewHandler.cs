using Beacon.Core.Helpers;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Queries;

internal sealed class ExecuteQueryPreviewHandler(IQueryExecutionPreviewService previewService)
    : IRequestHandler<ExecuteQueryPreviewCommand, QueryPreviewResult>
{
    public async Task<QueryPreviewResult> Handle(ExecuteQueryPreviewCommand request, CancellationToken cancellationToken)
    {
        var result = await previewService.ExecuteQueryPreview(request.QueryId, request.Draft, request, cancellationToken);

        if (result == null)
        {
            throw new InvalidOperationException($"Query #{request.QueryId} preview failed.");
        }

        return result;
    }
}

/// <summary>Runs the query and returns one page of its result; <c>sort</c> names a result column.</summary>
public record ExecuteQueryPreviewCommand : ListRequest, IRequest<QueryPreviewResult>
{
    public required int QueryId { get; init; }

    /// <summary>The editor's unsaved steps; when set they run instead of the stored query, which stays untouched.</summary>
    public QueryDraft? Draft { get; init; }
}

using Beacon.Core.Helpers;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Queries;

internal sealed class GetQueriesHandler(IQueryService queryService)
    : IRequestHandler<GetQueriesQuery, PagedList<QueryData>>
{
    public Task<PagedList<QueryData>> Handle(GetQueriesQuery request, CancellationToken cancellationToken)
    {
        var serviceRequest = new GetQueriesRequest
        {
            Page = request.Page,
            PageSize = request.PageSize,
            Sort = request.Sort,
            QueryId = request.QueryId,
            DataSourceId = request.DataSourceId,
            QueryName = request.QueryName,
            FolderId = request.FolderId,
            SearchTerm = request.SearchTerm,
        };

        return queryService.GetQueries(serviceRequest, cancellationToken);
    }
}

/// <summary>Newest first unless <c>sort</c> says otherwise.</summary>
public record GetQueriesQuery : ListRequest, IRequest<PagedList<QueryData>>
{
    public int? QueryId { get; init; }

    public int? DataSourceId { get; init; }

    public string? QueryName { get; init; }

    /// <summary>Null shows every folder; -1 shows only root-level queries.</summary>
    public int? FolderId { get; init; }

    /// <summary>Case-insensitive partial match on the name.</summary>
    public string? SearchTerm { get; init; }
}

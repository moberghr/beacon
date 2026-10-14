using Beacon.Core.Helpers;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Queries;

internal sealed class GetQueriesHandler(IQueryService queryService, IProjectAccessEvaluator projectAccess)
    : IRequestHandler<GetQueriesQuery, PagedList<QueryData>>
{
    public async Task<PagedList<QueryData>> Handle(GetQueriesQuery request, CancellationToken cancellationToken)
    {
        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        var serviceRequest = new GetQueriesRequest
        {
            Readable = scope.For(ProjectAction.Read),
            Page = request.Page,
            PageSize = request.PageSize,
            Sort = request.Sort,
            QueryId = request.QueryId,
            DataSourceId = request.DataSourceId,
            QueryName = request.QueryName,
            FolderId = request.FolderId,
            SearchTerm = request.SearchTerm,
        };

        return await queryService.GetQueries(serviceRequest, cancellationToken);
    }
}

/// <summary>Newest first unless <c>sort</c> says otherwise.</summary>
public record GetQueriesQuery : ListRequest, IRequest<PagedList<QueryData>>, IProjectFilteredRequest
{
    [ProjectScopeExempt("filter only: the list is scope-filtered")]
    public int? QueryId { get; init; }

    [ProjectScopeExempt("filter only: the list is scope-filtered")]

    public int? DataSourceId { get; init; }

    public string? QueryName { get; init; }

    /// <summary>Null shows every folder; -1 shows only root-level queries.</summary>
    [ProjectScopeExempt("filter only: the list is scope-filtered")]
    public int? FolderId { get; init; }

    /// <summary>Case-insensitive partial match on the name.</summary>
    public string? SearchTerm { get; init; }
}

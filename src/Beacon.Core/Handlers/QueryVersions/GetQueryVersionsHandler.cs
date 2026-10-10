using Beacon.Core.Helpers;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.QueryVersions;

internal sealed class GetQueryVersionsHandler(IQueryVersionService versionService, IProjectAccessEvaluator projectAccess)
    : IRequestHandler<GetQueryVersionsQuery, PagedList<QueryVersionSummary>>
{
    public async Task<PagedList<QueryVersionSummary>> Handle(GetQueryVersionsQuery request, CancellationToken cancellationToken)
    {
        var scope = await projectAccess.GetScopeAsync(cancellationToken);

        return await versionService.GetVersionsAsync(request.QueryId, request, scope.For(ProjectAction.Read), cancellationToken);
    }
}

/// <summary>
/// A query's versions, newest first unless <c>sort</c> says otherwise. <c>QueryId</c> binds from the route. Each version
/// is listed only where the caller may read it by the data sources it recorded, as its detail is.
/// </summary>
public record GetQueryVersionsQuery : ListRequest, IRequest<PagedList<QueryVersionSummary>>, IProjectAuthorizedRequest, IProjectFilteredRequest
{
    public int QueryId { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Query(QueryId), ProjectAction.Read)
    ];
}

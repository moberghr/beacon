using Beacon.Core.Helpers;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.QueryVersions;

internal sealed class GetQueryVersionsHandler(IQueryVersionService versionService)
    : IRequestHandler<GetQueryVersionsQuery, PagedList<QueryVersionSummary>>
{
    public Task<PagedList<QueryVersionSummary>> Handle(GetQueryVersionsQuery request, CancellationToken cancellationToken) =>
        versionService.GetVersionsAsync(request.QueryId, request, cancellationToken);
}

/// <summary>A query's versions, newest first unless <c>sort</c> says otherwise. <c>QueryId</c> binds from the route.</summary>
public record GetQueryVersionsQuery : ListRequest, IRequest<PagedList<QueryVersionSummary>>, IProjectAuthorizedRequest
{
    public int QueryId { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Query(QueryId), ProjectAction.Read)
    ];
}

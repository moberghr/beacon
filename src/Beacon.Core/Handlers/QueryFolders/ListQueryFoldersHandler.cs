using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Helpers;

namespace Beacon.Core.Handlers.QueryFolders;

/// <summary>
/// The paged form of <see cref="GetQueryFoldersQuery"/>: folders in tree order unless the request names a sort,
/// optionally matching a search on the name or path. Folders are project-less; their query counts cover only the queries
/// the caller may read.
/// </summary>
internal sealed class ListQueryFoldersHandler(IDbContextFactory<BeaconContext> contextFactory, IProjectAccessEvaluator projectAccess)
    : IRequestHandler<ListQueryFoldersQuery, PagedList<QueryFolderData>>
{
    public async Task<PagedList<QueryFolderData>> Handle(ListQueryFoldersQuery request, CancellationToken cancellationToken)
    {
        var readable = (await projectAccess.GetScopeAsync(cancellationToken)).For(ProjectAction.Read);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var readableQuery = ProjectAccessQueryableExtensions.AllSourcesInOneProjectOf<Query>(context, readable, x => x.Steps.Select(y => y.DataSourceId), x => x.OwnerUserId);

        return await context.QueryFolders
            .WhereIf(!string.IsNullOrWhiteSpace(request.Search), x => x.Name.Contains(request.Search!) || x.Path.Contains(request.Search!))
            .Select(x =>
                new QueryFolderData
                {
                    Id = x.Id,
                    Name = x.Name,
                    Description = x.Description,
                    ParentFolderId = x.ParentFolderId,
                    Path = x.Path,
                    SortOrder = x.SortOrder,
                    QueryCount = x.Queries
                        .AsQueryable()
                        .Count(readableQuery),
                    ChildFolderCount = x.ChildFolders.Count
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "sortOrder");
    }
}

public record ListQueryFoldersQuery : ListRequest, IRequest<PagedList<QueryFolderData>>, IProjectFilteredRequest
{
    public string? Search { get; init; }
}

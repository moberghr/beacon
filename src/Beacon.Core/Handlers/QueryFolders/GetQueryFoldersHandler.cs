using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data.Entities;

namespace Beacon.Core.Handlers.QueryFolders;

internal sealed class GetQueryFoldersHandler(IDbContextFactory<BeaconContext> contextFactory, IProjectAccessEvaluator projectAccess)
    : IRequestHandler<GetQueryFoldersQuery, GetQueryFoldersResult>
{
    public async Task<GetQueryFoldersResult> Handle(GetQueryFoldersQuery request, CancellationToken cancellationToken)
    {
        var readable = (await projectAccess.GetScopeAsync(cancellationToken)).For(ProjectAction.Read);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Folders are project-less; their query counts cover only the queries the caller may read.
        var readableQuery = ProjectAccessQueryableExtensions.AllSourcesInOneProjectOf<Query>(context, readable, x => x.Steps.Select(y => y.DataSourceId), x => x.OwnerUserId);

        var folders = await context.QueryFolders
            .OrderBy(f => f.SortOrder)
            .Select(f => new QueryFolderData
            {
                Id = f.Id,
                Name = f.Name,
                Description = f.Description,
                ParentFolderId = f.ParentFolderId,
                Path = f.Path,
                SortOrder = f.SortOrder,
                QueryCount = f.Queries
                    .AsQueryable()
                    .Count(readableQuery),
                ChildFolderCount = f.ChildFolders.Count
            })
            .ToListAsync(cancellationToken);

        // Count queries at root level (no folder assigned)
        var rootLevelQueryCount = await context.Queries
            .WhereAllSourcesInOneProject(context, readable, x => x.Steps.Select(y => y.DataSourceId), x => x.OwnerUserId)
            .Where(q => q.FolderId == null)
            .CountAsync(cancellationToken);

        // Count all queries
        var totalQueryCount = await context.Queries
            .WhereAllSourcesInOneProject(context, readable, x => x.Steps.Select(y => y.DataSourceId), x => x.OwnerUserId)
            .CountAsync(cancellationToken);

        return new GetQueryFoldersResult
        {
            Folders = folders,
            RootLevelQueryCount = rootLevelQueryCount,
            TotalQueryCount = totalQueryCount
        };
    }
}

public record GetQueryFoldersQuery() : IRequest<GetQueryFoldersResult>, IProjectFilteredRequest;

public record GetQueryFoldersResult
{
    public List<QueryFolderData> Folders { get; init; } = new();
    public int RootLevelQueryCount { get; init; }
    public int TotalQueryCount { get; init; }
}

public record QueryFolderData
{
    public int Id { get; init; }
    public string Name { get; init; } = null!;
    public string? Description { get; init; }
    public int? ParentFolderId { get; init; }
    public string Path { get; init; } = null!;
    public int SortOrder { get; init; }
    public int QueryCount { get; init; }
    public int ChildFolderCount { get; init; }
}

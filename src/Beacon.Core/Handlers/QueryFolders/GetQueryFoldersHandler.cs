using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;

namespace Beacon.Core.Handlers.QueryFolders;

internal sealed class GetQueryFoldersHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetQueryFoldersQuery, GetQueryFoldersResult>
{
    public async Task<GetQueryFoldersResult> Handle(GetQueryFoldersQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

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
                QueryCount = f.Queries.Count,
                ChildFolderCount = f.ChildFolders.Count
            })
            .ToListAsync(cancellationToken);

        // Count queries at root level (no folder assigned)
        var rootLevelQueryCount = await context.Queries
            .Where(q => q.FolderId == null)
            .CountAsync(cancellationToken);

        // Count all queries
        var totalQueryCount = await context.Queries
            .CountAsync(cancellationToken);

        return new GetQueryFoldersResult
        {
            Folders = folders,
            RootLevelQueryCount = rootLevelQueryCount,
            TotalQueryCount = totalQueryCount
        };
    }
}

public record GetQueryFoldersQuery() : IRequest<GetQueryFoldersResult>;

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

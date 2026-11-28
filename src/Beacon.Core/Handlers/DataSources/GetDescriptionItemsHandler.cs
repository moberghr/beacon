using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;

namespace Beacon.Core.Handlers.DataSources;

/// <summary>
/// The table and column descriptions Beacon wrote into a data source's catalog, one row each: who proposed it (curated or
/// generated), who applied it and when, from which patch and model, and since when it is stale. The description text is not
/// returned. Paged in SQL, by schema, table and column unless the request names a sort.
/// </summary>
internal sealed class GetDescriptionItemsHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetDescriptionItemsQuery, PagedList<DescriptionItem>>
{
    public async Task<PagedList<DescriptionItem>> Handle(GetDescriptionItemsQuery request, CancellationToken cancellationToken)
    {
        if (request.DataSourceId <= 0)
        {
            throw new InvalidOperationException("Data source id must be positive.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.McpDescriptionItems
            .AsNoTracking()
            .Where(x => x.DataSourceId == request.DataSourceId)
            .Select(x =>
                new DescriptionItem
                {
                    Id = x.Id,
                    SchemaName = x.SchemaName,
                    TableName = x.TableName,
                    ColumnName = x.ColumnName,
                    Source = x.Source,
                    WrittenAt = x.WrittenAt,
                    WrittenBy = context.Users
                        .Where(y => y.Id == x.WrittenByUserId)
                        .Select(UserLabel.OfUser)
                        .FirstOrDefault(),
                    GeneratedByModel = x.GeneratedByModel,
                    SourcePatchId = x.SourcePatchId,
                    StaleSince = x.StaleSince,
                    IsStale = x.StaleSince != null
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "schemaName,tableName,columnName");
    }
}

/// <summary>
/// A data source's description items, ordered by schema, table and column unless <c>sort</c> says otherwise.
/// <c>DataSourceId</c> binds from the route.
/// </summary>
public record GetDescriptionItemsQuery : ListRequest, IRequest<PagedList<DescriptionItem>>, IProjectAuthorizedRequest
{
    public int DataSourceId { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.DataSource(DataSourceId), ProjectAction.Read)
    ];
}

/// <summary>
/// One description Beacon wrote: the table's (<see cref="ColumnName"/> empty) or a column's. <see cref="WrittenBy"/> is the
/// applying user's label (display name, else "User {id}"), null when no user was recorded or the user is gone.
/// </summary>
public record DescriptionItem
{
    public int Id { get; init; }
    public required string SchemaName { get; init; }
    public required string TableName { get; init; }
    public required string ColumnName { get; init; }
    public McpDescriptionSource Source { get; init; }
    public DateTime WrittenAt { get; init; }
    public string? WrittenBy { get; init; }
    public string? GeneratedByModel { get; init; }
    public int? SourcePatchId { get; init; }
    public DateTime? StaleSince { get; init; }
    public bool IsStale { get; init; }
}

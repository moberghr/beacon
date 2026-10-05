using Beacon.Core.Data;
using Beacon.Core.Helpers;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.DataSources;

internal sealed class GetDataSourcesHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetDataSourcesQuery, PagedList<DataSourceEntry>>
{
    public async Task<PagedList<DataSourceEntry>> Handle(GetDataSourcesQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // No redundant `ArchivedTime == null` here: the global soft-delete filter already applies it, and
        // combining the two on EF Core 9 / Npgsql collapses the predicate to WHERE FALSE.
        return await context.DataSources
            .WhereIf(!string.IsNullOrWhiteSpace(request.Search), x => x.Name.Contains(request.Search!))
            .WhereIf(request.DatabaseOnly == true, x => x.DatabaseEngineType != null)
            .Select(x =>
                new DataSourceEntry
                {
                    Id = x.Id,
                    Name = x.Name,
                    DataSourceType = x.DataSourceType.ToString(),
                    DatabaseEngineType = x.DatabaseEngineType.HasValue ? x.DatabaseEngineType.Value.ToString() : null,
                    QueryCount = x.QuerySteps
                        .Where(y => y.Query.ArchivedTime == null)
                        .Select(y => y.QueryId)
                        .Distinct()
                        .Count(),
                    MigrationJobsCount = context.MigrationJobs
                        .Where(y => y.DataSourceId == x.Id || y.DestinationDataSourceId == x.Id)
                        .Count(),
                    MetadataLoadingEnabled = x.MetadataLoadingEnabled,
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "name");
    }
}

/// <summary>Alphabetical unless <c>sort</c> says otherwise; <c>search</c> matches the name — pickers send it as the user types.</summary>
public record GetDataSourcesQuery : ListRequest, IRequest<PagedList<DataSourceEntry>>
{
    public string? Search { get; init; }

    /// <summary>Only sources with a database engine (SQL targets), e.g. for migrations.</summary>
    public bool? DatabaseOnly { get; init; }
}

public record DataSourceEntry
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string DataSourceType { get; init; } = string.Empty;

    public string? DatabaseEngineType { get; init; }

    public int QueryCount { get; init; }

    public int MigrationJobsCount { get; init; }

    public bool MetadataLoadingEnabled { get; init; }
}

using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.DataMigration;

internal sealed class GetMigrationJobsHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetMigrationJobsQuery, PagedList<MigrationJobListItem>>
{
    public async Task<PagedList<MigrationJobListItem>> Handle(
        GetMigrationJobsQuery request,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.MigrationJobs
            .WhereIf(!string.IsNullOrWhiteSpace(request.Search), x => x.Name.Contains(request.Search!))
            .Select(x =>
                new MigrationJobListItem
                {
                    Id = x.Id,
                    Name = x.Name,
                    Description = x.Description,
                    DataSourceId = x.DataSourceId,
                    DataSourceName = x.DataSource.Name,
                    DestinationDataSourceId = x.DestinationDataSourceId,
                    DestinationDataSourceName = x.DestinationDataSource.Name,
                    DestinationTable = x.DestinationTable,
                    Mode = x.Mode,
                    IsEnabled = x.IsEnabled,
                    Schedule = x.Schedule,
                    CreatedTime = x.CreatedTime,
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-createdTime");
    }
}

/// <summary>Newest first unless <c>sort</c> says otherwise; <c>search</c> matches the job name.</summary>
public record GetMigrationJobsQuery : ListRequest, IRequest<PagedList<MigrationJobListItem>>
{
    public string? Search { get; init; }
}

public record MigrationJobListItem
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public int DataSourceId { get; init; }

    public string DataSourceName { get; init; } = string.Empty;

    public int DestinationDataSourceId { get; init; }

    public string DestinationDataSourceName { get; init; } = string.Empty;

    public string DestinationTable { get; init; } = string.Empty;

    public MigrationMode Mode { get; init; }

    public bool IsEnabled { get; init; }

    public string? Schedule { get; init; }

    public DateTime CreatedTime { get; init; }
}

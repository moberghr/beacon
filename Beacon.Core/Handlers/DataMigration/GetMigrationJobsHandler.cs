using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.DataMigration;

internal sealed class GetMigrationJobsHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetMigrationJobsQuery, GetMigrationJobsResult>
{
    public async Task<GetMigrationJobsResult> Handle(
        GetMigrationJobsQuery request,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var jobs = await context.MigrationJobs
            .OrderByDescending(x => x.CreatedTime)
            .Select(x =>
                new MigrationJobListItem(
                    x.Id,
                    x.Name,
                    x.Description,
                    x.DataSourceId,
                    x.DataSource.Name,
                    x.DestinationDataSourceId,
                    x.DestinationDataSource.Name,
                    x.DestinationTable,
                    x.Mode,
                    x.IsEnabled,
                    x.Schedule,
                    x.CreatedTime))
            .ToListAsync(cancellationToken);

        return new GetMigrationJobsResult(jobs);
    }
}

public record GetMigrationJobsQuery() : IRequest<GetMigrationJobsResult>;

public record GetMigrationJobsResult(IReadOnlyList<MigrationJobListItem> Jobs);

public record MigrationJobListItem(
    int Id,
    string Name,
    string Description,
    int DataSourceId,
    string DataSourceName,
    int DestinationDataSourceId,
    string DestinationDataSourceName,
    string DestinationTable,
    MigrationMode Mode,
    bool IsEnabled,
    string? Schedule,
    DateTime CreatedTime);

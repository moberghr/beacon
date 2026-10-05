using Beacon.Core.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.DataSources;

internal sealed class GetDataSourceHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetDataSourceQuery, DataSourceEntry?>
{
    public async Task<DataSourceEntry?> Handle(GetDataSourceQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.DataSources
            .Where(x => x.Id == request.Id)
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
            .FirstOrDefaultAsync(cancellationToken);
    }
}

public record GetDataSourceQuery(int Id) : IRequest<DataSourceEntry?>;

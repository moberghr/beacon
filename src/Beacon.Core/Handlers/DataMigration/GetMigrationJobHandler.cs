using Beacon.Core.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.DataMigration;

internal sealed class GetMigrationJobHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetMigrationJobQuery, MigrationJobListItem?>
{
    public async Task<MigrationJobListItem?> Handle(GetMigrationJobQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.MigrationJobs
            .Where(x => x.Id == request.Id)
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
            .FirstOrDefaultAsync(cancellationToken);
    }
}

public record GetMigrationJobQuery(int Id) : IRequest<MigrationJobListItem?>;

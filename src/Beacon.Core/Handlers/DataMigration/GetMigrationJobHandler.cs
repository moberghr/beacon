using Beacon.Core.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;

namespace Beacon.Core.Handlers.DataMigration;

internal sealed class GetMigrationJobHandler(IDbContextFactory<BeaconContext> contextFactory, IProjectAccessEvaluator projectAccess)
    : IRequestHandler<GetMigrationJobQuery, MigrationJobListItem?>
{
    public async Task<MigrationJobListItem?> Handle(GetMigrationJobQuery request, CancellationToken cancellationToken)
    {
        var readable = (await projectAccess.GetScopeAsync(cancellationToken)).For(ProjectAction.Read);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // A job whose data sources the caller cannot both read is not found, as in the list.
        return await context.MigrationJobs
            .Where(x => x.Id == request.Id)
            .WhereSourceAccessible(context, readable, x => x.DataSourceId)
            .WhereSourceAccessible(context, readable, x => x.DestinationDataSourceId)
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

[ProjectScopeExempt("gated by the MigrationOperator permission; a job is found only when the caller reads both of its data sources", Policy = BeaconPermissions.MigrationOperatorPolicy)]
public record GetMigrationJobQuery(int Id) : IRequest<MigrationJobListItem?>;

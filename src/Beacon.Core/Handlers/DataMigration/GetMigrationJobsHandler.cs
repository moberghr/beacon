using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;

namespace Beacon.Core.Handlers.DataMigration;

internal sealed class GetMigrationJobsHandler(IDbContextFactory<BeaconContext> contextFactory, IProjectAccessEvaluator projectAccess)
    : IRequestHandler<GetMigrationJobsQuery, PagedList<MigrationJobListItem>>
{
    public async Task<PagedList<MigrationJobListItem>> Handle(
        GetMigrationJobsQuery request,
        CancellationToken cancellationToken)
    {
        var readable = (await projectAccess.GetScopeAsync(cancellationToken)).For(ProjectAction.Read);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The jobs whose data sources the caller reads at both ends (an Admin's: every job).
        return await context.MigrationJobs
            .WhereSourceAccessible(context, readable, x => x.DataSourceId)
            .WhereSourceAccessible(context, readable, x => x.DestinationDataSourceId)
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

/// <summary>
/// Newest first unless <c>sort</c> says otherwise; <c>search</c> matches the job name. Only the jobs whose source and
/// destination data sources the caller reads.
/// </summary>
[ProjectScopeExempt("gated by the MigrationOperator permission; the list is filtered to jobs whose data sources the caller reads", Policy = BeaconPermissions.MigrationOperatorPolicy)]
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

using Beacon.Core.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.DataSources;

internal sealed class GetDataSourceHandler(IDbContextFactory<BeaconContext> contextFactory, IProjectAccessEvaluator projectAccess)
    : IRequestHandler<GetDataSourceQuery, DataSourceEntry?>
{
    public async Task<DataSourceEntry?> Handle(GetDataSourceQuery request, CancellationToken cancellationToken)
    {
        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        var readable = scope.For(ProjectAction.Read);
        var shownProject = DataSourceProjects.Shown(readable);
        var otherProject = DataSourceProjects.Other(readable);
        var countsMigrationJobs = scope.IsUnrestricted;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var readableStep = DataSourceProjects.ReadableStep(context, readable);

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
                        .AsQueryable()
                        .Where(y => y.Query.ArchivedTime == null)
                        .Where(readableStep)
                        .Select(y => y.QueryId)
                        .Distinct()
                        .Count(),
                    MigrationJobsCount = countsMigrationJobs
                        ? context.MigrationJobs
                            .Where(y => y.DataSourceId == x.Id || y.DestinationDataSourceId == x.Id)
                            .Count()
                        : 0,
                    MetadataLoadingEnabled = x.MetadataLoadingEnabled,
                    OpenWhenUnlinked = x.OpenWhenUnlinked,
                    Projects = context.ProjectDataSources
                        .Where(y => y.DataSourceId == x.Id)
                        .Where(shownProject)
                        .Select(y =>
                            new DataSourceProjectChip
                            {
                                Id = y.ProjectId,
                                Name = y.Project.Name
                            })
                        .ToList(),
                    OtherProjectCount = context.Projects
                        .Where(y => y.DataSources.Any(z => z.DataSourceId == x.Id))
                        .Count(otherProject)
                })
            .FirstOrDefaultAsync(cancellationToken);
    }
}

public record GetDataSourceQuery(int Id) : IRequest<DataSourceEntry?>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.DataSource(Id), ProjectAction.Read)
    ];
}

using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.DataSources;
using Beacon.Core.Services;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Handlers.DataSources;

internal sealed class CreateDataSourceHandler(
    IDataSourceService dataSourceService,
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess,
    IBeaconActorAccessor actorAccessor,
    ILogger<CreateDataSourceHandler> logger)
    : IRequestHandler<CreateDataSourceCommand, CreateDataSourceResult>
{
    public async Task<CreateDataSourceResult> Handle(CreateDataSourceCommand request, CancellationToken cancellationToken)
    {
        var projectIds = (request.ProjectIds ?? [])
            .Distinct()
            .ToList();
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        if (projectIds.Count > 0)
        {
            await EnsureLinkableIntoAsync(projectIds, actor, cancellationToken);
        }

        var data = new DataSourceData
        {
            Name = request.Name,
            DataSourceType = request.DataSourceType,
            DatabaseEngineType = request.DatabaseEngineType,
            ConnectionString = request.ConnectionString,
            MetadataLoadingEnabled = request.MetadataLoadingEnabled,
            MetadataMaxTables = request.MetadataMaxTables,
            MetadataMaxColumnsPerTable = request.MetadataMaxColumnsPerTable,
            MetadataLoadTableNamesOnly = request.MetadataLoadTableNamesOnly,
            MetadataExcludeSchemas = request.MetadataExcludeSchemas ?? new List<string>(),
            MetadataIncludeSchemas = request.MetadataIncludeSchemas ?? new List<string>(),
            ProjectIds = projectIds,
            LinkedByUserId = actor.BeaconUserId
        };

        var response = await dataSourceService.CreateDataSource(data, cancellationToken);

        if (!response.Success)
        {
            throw new InvalidOperationException(response.Message);
        }

        return new CreateDataSourceResult(true, response.Message);
    }

    // Each target project must be live, and the caller must manage its data sources (an Admin does, for every project).
    private async Task EnsureLinkableIntoAsync(List<int> projectIds, BeaconActor actor, CancellationToken cancellationToken)
    {
        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var liveProjectIds = await context.Projects
            .Where(x => projectIds.Contains(x.Id))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        foreach (var projectId in projectIds)
        {
            if (!liveProjectIds.Contains(projectId))
            {
                throw ProjectAccessGuard.NotFound(logger, actor, projectId, ProjectAction.ManageMembers, AccessRefusal.Missing);
            }

            ProjectAccessGuard.Demand(scope, projectId, ProjectAction.ManageMembers, actor, logger);
        }
    }
}

/// <summary>
/// Creates a data source (Admin). It is reachable by Admins only until a project links it; <c>ProjectIds</c> links it
/// into those projects as it is created.
/// </summary>
[ProjectScopeExempt("admin-gated data-source administration; the link rules check ProjectIds in the handler", Policy = ProjectScopeExemptAttribute.AdminPolicy)]
public record CreateDataSourceCommand(
    string Name,
    DataSourceType DataSourceType,
    DatabaseEngineType? DatabaseEngineType,
    string ConnectionString,
    bool MetadataLoadingEnabled,
    int MetadataMaxTables,
    int MetadataMaxColumnsPerTable,
    bool MetadataLoadTableNamesOnly,
    List<string>? MetadataExcludeSchemas,
    List<string>? MetadataIncludeSchemas,
    List<int>? ProjectIds = null) : IRequest<CreateDataSourceResult>;

public record CreateDataSourceResult(bool Success, string Message);

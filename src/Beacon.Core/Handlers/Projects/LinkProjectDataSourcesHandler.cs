using System.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.HostData;
using Beacon.Core.Models;

namespace Beacon.Core.Handlers.Projects;

internal sealed class LinkProjectDataSourcesHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess,
    IBeaconActorAccessor actorAccessor,
    IHostDataSourceRegistry hostSources,
    IOptions<ProjectAuthorizationOptions> options,
    ILogger<LinkProjectDataSourcesHandler> logger)
    : IRequestHandler<LinkProjectDataSourcesCommand, LinkProjectDataSourcesResult>
{
    public async Task<LinkProjectDataSourcesResult> Handle(LinkProjectDataSourcesCommand request, CancellationToken cancellationToken)
    {
        var dataSourceIds = (request.DataSourceIds ?? [])
            .Distinct()
            .ToList();
        if (dataSourceIds.Count == 0)
        {
            throw new InvalidOperationException("Name at least one data source to link.");
        }

        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

        try
        {
            return await LinkAsync(request.ProjectId, dataSourceIds, scope, actor, cancellationToken);
        }
        catch (Exception ex) when (DbSerializationConflict.IsSerializationConflict(ex))
        {
            throw new BeaconConflictException("The data sources' links changed concurrently; retry.");
        }
    }

    private async Task<LinkProjectDataSourcesResult> LinkAsync(
        int projectId,
        List<int> dataSourceIds,
        ProjectAccessScope scope,
        BeaconActor actor,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var project = await ProjectMembershipRules.RequireManageMembersAsync(context, scope, projectId, actor, logger, cancellationToken);

        // The links are read and written in one serializable transaction, so a source restricted or unlinked meanwhile
        // is never linked on stale reads.
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var candidates = await ProjectDataSourceLinks.ReadAsync(context, dataSourceIds, cancellationToken);
        ProjectDataSourceLinks.Check(
            scope,
            dataSourceIds,
            candidates,
            project.HostManagedKey,
            ProjectDataSourceLinks.HostProjectKeys(hostSources),
            options.Value.RequireProjectMembership,
            actor,
            logger);

        var linked = candidates
            .Where(x => x.Links.All(y => y.ProjectId != project.Id))
            .Select(x => x.DataSourceId)
            .Order()
            .ToList();
        if (linked.Count == 0)
        {
            return new LinkProjectDataSourcesResult(project.Id, []);
        }

        foreach (var dataSourceId in linked)
        {
            context.ProjectDataSources.Add(new ProjectDataSource { ProjectId = project.Id, DataSourceId = dataSourceId, LinkedByUserId = actor.BeaconUserId });
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
        {
            throw new BeaconConflictException("A data source was linked to this project concurrently; reload and retry.");
        }

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            ProjectAccessEvents.DataSourcesLinked,
            "Data sources linked: project {ProjectId}, data sources {DataSourceIds}, by user {ActorId}",
            project.Id,
            linked,
            actor.LogId);

        return new LinkProjectDataSourcesResult(project.Id, linked);
    }
}

/// <summary>
/// Links existing data sources into a project. For a ProjectAdmin of the project or an Admin. A non-Admin links only
/// data sources a live open project already holds; a host's data source links only into the project the host declares
/// for it. 404 for a data source that is missing or unreadable to the caller, 403 for one it may read but not link, 409
/// when the links change concurrently (retry). Already linked sources are left as they are.
/// </summary>
public record LinkProjectDataSourcesCommand(int ProjectId, [property: ProjectScopeExempt("save-time: the link rules check each data source inside the handler's transaction (404 unreadable, 403 not linkable)")] List<int> DataSourceIds) : IRequest<LinkProjectDataSourcesResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Project(ProjectId), ProjectAction.ManageMembers)
    ];
}

/// <summary>The data sources this request linked (not those that were linked already).</summary>
public record LinkProjectDataSourcesResult(int ProjectId, List<int> LinkedDataSourceIds);

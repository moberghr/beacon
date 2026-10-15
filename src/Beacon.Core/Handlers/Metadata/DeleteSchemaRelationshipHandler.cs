using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Models;
using Beacon.Core.Services.Metadata;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Handlers.Metadata;

/// <summary>
/// Removes (archives) a relationship. A verified relationship is a join every project linking the source is told to trust,
/// so removing one needs what verifying it needs — Curate in every live project linking the source, or a DataSourceAdmin
/// (<see cref="GroundingCuration"/>); 403 otherwise. An unverified one needs only the route's check. The relationship is
/// archived only while it still holds the label, cardinality and verification read — a verification or a change between
/// the read and the write refuses the removal (409) — so a relationship verified meanwhile is never removed on the strength
/// of a check made while it was unverified.
/// </summary>
internal sealed class DeleteSchemaRelationshipHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    ISchemaGraphService schemaGraphService,
    IProjectAccessEvaluator projectAccess,
    IBeaconPermissionAccessor permissionAccessor,
    IBeaconActorAccessor actorAccessor,
    ILogger<DeleteSchemaRelationshipHandler> logger)
    : IRequestHandler<DeleteSchemaRelationshipCommand>
{
    internal const string VerifiedNeedsCuratorMessage =
        "Removing a verified relationship needs the project admin role in every project that links the data source, or the DataSourceAdmin permission.";

    public async Task Handle(DeleteSchemaRelationshipCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The relationship must belong to the route's data source, the one the caller's access was checked on.
        var relationship = await context.SchemaRelationships
            .Where(x => x.Id == request.Id)
            .Where(x => x.DataSourceId == request.DataSourceId)
            .Select(x =>
                new
                {
                    x.Label,
                    x.Cardinality,
                    x.IsVerified
                })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconNotFoundException($"Schema relationship {request.Id} not found.");

        if (relationship.IsVerified && !await MayCurateAsync(context, request.DataSourceId, cancellationToken))
        {
            var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

            throw AccessRefusal.Of(logger, actor, "schema relationship", request.Id, VerifiedNeedsCuratorMessage);
        }

        // Soft delete (§2.14) — the global query filter takes it out of the graph, and re-running sync
        // will not resurrect a foreign-key edge a user deliberately removed.
        var archivedTime = DateTime.UtcNow;
        var archived = await context.SchemaRelationships
            .Where(x => x.Id == request.Id)
            .Where(x => x.DataSourceId == request.DataSourceId)
            .Where(x => x.Label == relationship.Label)
            .Where(x => x.Cardinality == relationship.Cardinality)
            .Where(x => x.IsVerified == relationship.IsVerified)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.ArchivedTime, archivedTime), cancellationToken);
        if (archived == 0)
        {
            throw new BeaconConflictException(UpdateSchemaRelationshipHandler.ChangedSinceLoadedMessage);
        }

        schemaGraphService.Invalidate(request.DataSourceId);
    }

    private async Task<bool> MayCurateAsync(BeaconContext context, int dataSourceId, CancellationToken cancellationToken)
    {
        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        var shared = await GroundingCuration.OfSharedSourcesAsync(context, scope, permissionAccessor, [dataSourceId], cancellationToken);

        return shared.Allows(dataSourceId, shared.WritesSource(dataSourceId));
    }
}

/// <summary>
/// Removes a relationship of the data source <c>DataSourceId</c>; 404 when it is not that source's. A verified one needs a
/// curator of the source (403 otherwise); one verified or changed meanwhile is not removed (409).
/// </summary>
public record DeleteSchemaRelationshipCommand(
    int DataSourceId,
    [property: ProjectScopeExempt("bound to the route's data source by the handler (404 when it is another source's)")] int Id) : IRequest, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        ProjectCheck.SharedSource(ProjectResource.DataSource(DataSourceId), ProjectAction.Manage)
    ];
}

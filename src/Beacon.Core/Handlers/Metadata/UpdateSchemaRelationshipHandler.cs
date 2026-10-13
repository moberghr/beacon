using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Services.Metadata;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Metadata;

/// <summary>
/// Relabels a relationship. A verified relationship is a join every project linking the source is told to trust, so a
/// change to one by a caller who may not curate the source's grounding (<see cref="GroundingCuration"/>) stores it
/// unverified again, waiting for a curator; a curator's change keeps it verified and records the curator as its verifier
/// (a curator without a Beacon user — whose verification cannot be recorded — stores it unverified too). The result says
/// whether the relationship is verified after the change.
/// </summary>
internal sealed class UpdateSchemaRelationshipHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    ISchemaGraphService schemaGraphService,
    IProjectAccessEvaluator projectAccess,
    IBeaconPermissionAccessor permissionAccessor,
    IBeaconActorAccessor actorAccessor)
    : IRequestHandler<UpdateSchemaRelationshipCommand, UpdateSchemaRelationshipResult>
{
    public async Task<UpdateSchemaRelationshipResult> Handle(UpdateSchemaRelationshipCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The relationship must belong to the route's data source, the one the caller's access was checked on.
        var relationship = await context.SchemaRelationships
            .Where(x => x.Id == request.Id)
            .Where(x => x.DataSourceId == request.DataSourceId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconNotFoundException($"Schema relationship {request.Id} not found.");

        var label = string.IsNullOrWhiteSpace(request.Label) ? relationship.Label : request.Label;
        var changes = label != relationship.Label || request.Cardinality != relationship.Cardinality;
        relationship.Label = label;
        relationship.Cardinality = request.Cardinality;

        if (changes && relationship.IsVerified)
        {
            var curator = await CuratorAsync(context, relationship.DataSourceId, cancellationToken);
            relationship.IsVerified = curator != null;
            relationship.VerifiedByUserId = curator;
            relationship.VerifiedTime = curator == null ? null : DateTime.UtcNow;
        }

        await context.SaveChangesAsync(cancellationToken);

        schemaGraphService.Invalidate(relationship.DataSourceId);

        return new UpdateSchemaRelationshipResult(relationship.IsVerified);
    }

    // The caller's Beacon user when it may curate the source's shared grounding; null otherwise.
    private async Task<int?> CuratorAsync(BeaconContext context, int dataSourceId, CancellationToken cancellationToken)
    {
        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        var shared = await GroundingCuration.OfSharedSourcesAsync(context, scope, permissionAccessor, [dataSourceId], cancellationToken);
        if (!shared.Allows(dataSourceId, shared.WritesSource(dataSourceId)))
        {
            return null;
        }

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

        return actor.BeaconUserId;
    }
}

/// <summary>
/// Relabels a relationship of the data source <c>DataSourceId</c>; 404 when the relationship is not that source's. A change
/// to a verified relationship by a caller who may not curate the source stores it unverified.
/// </summary>
public record UpdateSchemaRelationshipCommand : IRequest<UpdateSchemaRelationshipResult>, IProjectAuthorizedRequest
{
    public int DataSourceId { get; init; }

    [ProjectScopeExempt("bound to the route's data source by the handler (404 when it is another source's)")]
    public int Id { get; init; }

    public string? Label { get; init; }

    public SchemaRelationshipCardinality Cardinality { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        ProjectCheck.SharedSource(ProjectResource.DataSource(DataSourceId), ProjectAction.Write)
    ];
}

/// <param name="IsVerified">
/// Whether the relationship is verified after the change: false when a verified relationship was changed by a caller who
/// may not curate the source, so it waits for a curator again.
/// </param>
public record UpdateSchemaRelationshipResult(bool IsVerified);

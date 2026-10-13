using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Models;
using Beacon.Core.Services.Metadata;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Handlers.Metadata;

/// <summary>
/// Marks a relationship verified (recording the verifier, the caller) or unverified. Who verified is recorded, so a caller
/// without a Beacon user is refused (403).
/// </summary>
internal sealed class VerifySchemaRelationshipHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    ISchemaGraphService schemaGraphService,
    IBeaconActorAccessor actorAccessor,
    ILogger<VerifySchemaRelationshipHandler> logger)
    : IRequestHandler<VerifySchemaRelationshipCommand>
{
    internal const string UnidentifiedVerifierMessage = "Verifying a relationship needs a signed-in Beacon user: who verified it is recorded.";

    public async Task Handle(VerifySchemaRelationshipCommand request, CancellationToken cancellationToken)
    {
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        if (actor.BeaconUserId == null)
        {
            throw AccessRefusal.Of(logger, actor, "schema relationship", request.Id, UnidentifiedVerifierMessage);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The relationship must belong to the route's data source, the one the caller's access was checked on.
        var relationship = await context.SchemaRelationships
            .Where(x => x.Id == request.Id)
            .Where(x => x.DataSourceId == request.DataSourceId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconNotFoundException($"Schema relationship {request.Id} not found.");

        relationship.IsVerified = request.IsVerified;
        relationship.VerifiedByUserId = request.IsVerified ? actor.BeaconUserId : null;
        relationship.VerifiedTime = request.IsVerified ? DateTime.UtcNow : null;

        await context.SaveChangesAsync(cancellationToken);

        // Verification changes which prompt block the edge renders under, so the graph must be rebuilt.
        schemaGraphService.Invalidate(relationship.DataSourceId);
    }
}

/// <summary>
/// Marks a relationship of the data source <c>DataSourceId</c> verified or not; 404 when it is not that source's. A verified
/// relationship grounds every project linking the source, so this needs Curate in each of them, or a DataSourceAdmin.
/// </summary>
public record VerifySchemaRelationshipCommand(
    int DataSourceId,
    [property: ProjectScopeExempt("bound to the route's data source by the handler (404 when it is another source's)")] int Id,
    bool IsVerified) : IRequest, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        ProjectCheck.CurateShared(ProjectResource.DataSource(DataSourceId))
    ];
}

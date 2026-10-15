using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Services.Metadata;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Handlers.Metadata;

/// <summary>
/// Marks a relationship verified (recording the verifier, the caller) or unverified. Who verified is recorded, so a caller
/// without a Beacon user is refused (403). A verification applies to the content the curator reviewed: the command names
/// the label and cardinality the curator saw, and the relationship is verified only while it holds them — compared when it
/// is read and again in the statement that verifies it — so a change between the curator's view and the verification is
/// never verified unseen (409).
/// </summary>
internal sealed class VerifySchemaRelationshipHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    ISchemaGraphService schemaGraphService,
    IBeaconActorAccessor actorAccessor,
    ILogger<VerifySchemaRelationshipHandler> logger)
    : IRequestHandler<VerifySchemaRelationshipCommand>
{
    internal const string UnidentifiedVerifierMessage = "Verifying a relationship needs a signed-in Beacon user: who verified it is recorded.";

    internal const string ReviewedContentRequiredMessage =
        "Verifying a relationship needs the label and cardinality it was reviewed with.";

    public async Task Handle(VerifySchemaRelationshipCommand request, CancellationToken cancellationToken)
    {
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        if (actor.BeaconUserId == null)
        {
            throw AccessRefusal.Of(logger, actor, "schema relationship", request.Id, UnidentifiedVerifierMessage);
        }

        if (request.IsVerified && (request.ReviewedLabel == null || request.ReviewedCardinality == null))
        {
            throw new InvalidOperationException(ReviewedContentRequiredMessage);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The relationship must belong to the route's data source, the one the caller's access was checked on.
        var relationship = await context.SchemaRelationships
            .Where(x => x.Id == request.Id)
            .Where(x => x.DataSourceId == request.DataSourceId)
            .Select(x =>
                new
                {
                    x.Label,
                    x.Cardinality
                })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconNotFoundException($"Schema relationship {request.Id} not found.");

        // A verification applies to what the curator reviewed: the read must show it, and the statement compares it again.
        if (request.IsVerified && (relationship.Label != request.ReviewedLabel || relationship.Cardinality != request.ReviewedCardinality))
        {
            throw new BeaconConflictException(UpdateSchemaRelationshipHandler.ChangedSinceLoadedMessage);
        }

        var verifiedTime = DateTime.UtcNow;
        var written = request.IsVerified
            ? await context.SchemaRelationships
                .Where(x => x.Id == request.Id)
                .Where(x => x.DataSourceId == request.DataSourceId)
                .Where(x => x.Label == relationship.Label)
                .Where(x => x.Cardinality == relationship.Cardinality)
                .ExecuteUpdateAsync(
                    x => x
                        .SetProperty(y => y.IsVerified, true)
                        .SetProperty(y => y.VerifiedByUserId, actor.BeaconUserId)
                        .SetProperty(y => y.VerifiedTime, verifiedTime),
                    cancellationToken)
            : await context.SchemaRelationships
                .Where(x => x.Id == request.Id)
                .Where(x => x.DataSourceId == request.DataSourceId)
                .ExecuteUpdateAsync(
                    x => x
                        .SetProperty(y => y.IsVerified, false)
                        .SetProperty(y => y.VerifiedByUserId, (int?)null)
                        .SetProperty(y => y.VerifiedTime, (DateTime?)null),
                    cancellationToken);
        if (written == 0)
        {
            throw new BeaconConflictException(UpdateSchemaRelationshipHandler.ChangedSinceLoadedMessage);
        }

        // Verification changes which prompt block the edge renders under, so the graph must be rebuilt.
        schemaGraphService.Invalidate(request.DataSourceId);
    }
}

/// <summary>
/// Marks a relationship of the data source <c>DataSourceId</c> verified or not; 404 when it is not that source's. A verified
/// relationship grounds every project linking the source, so this needs Curate in each of them, or a DataSourceAdmin.
/// Verifying names what the curator reviewed (<see cref="ReviewedLabel"/>, <see cref="ReviewedCardinality"/>; 400 without
/// them), and a relationship that holds other content by then is not verified (409).
/// </summary>
public record VerifySchemaRelationshipCommand(
    int DataSourceId,
    [property: ProjectScopeExempt("bound to the route's data source by the handler (404 when it is another source's)")] int Id,
    bool IsVerified) : IRequest, IProjectAuthorizedRequest
{
    /// <summary>The relationship's label as the curator reviewed it; required to verify, ignored to unverify.</summary>
    public string? ReviewedLabel { get; init; }

    /// <summary>The relationship's cardinality as the curator reviewed it; required to verify, ignored to unverify.</summary>
    public SchemaRelationshipCardinality? ReviewedCardinality { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        ProjectCheck.CurateShared(ProjectResource.DataSource(DataSourceId))
    ];
}

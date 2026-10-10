using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Models;
using Beacon.Core.Services.Metadata;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Metadata;

internal sealed class VerifySchemaRelationshipHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    ISchemaGraphService schemaGraphService,
    IBeaconUserContext userContext)
    : IRequestHandler<VerifySchemaRelationshipCommand>
{
    public async Task Handle(VerifySchemaRelationshipCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The relationship must belong to the route's data source, the one the caller's access was checked on.
        var relationship = await context.SchemaRelationships
            .Where(x => x.Id == request.Id)
            .Where(x => x.DataSourceId == request.DataSourceId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconNotFoundException($"Schema relationship {request.Id} not found.");

        relationship.IsVerified = request.IsVerified;
        relationship.VerifiedByUserId = request.IsVerified ? ParseUserId(userContext.UserId) : null;
        relationship.VerifiedTime = request.IsVerified ? DateTime.UtcNow : null;

        await context.SaveChangesAsync(cancellationToken);

        // Verification changes which prompt block the edge renders under, so the graph must be rebuilt.
        schemaGraphService.Invalidate(relationship.DataSourceId);
    }

    private static int? ParseUserId(string? userId) =>
        int.TryParse(userId, out var parsed) ? parsed : null;
}

/// <summary>Marks a relationship of the data source <c>DataSourceId</c> verified or not; 404 when it is not that source's.</summary>
public record VerifySchemaRelationshipCommand(
    int DataSourceId,
    [property: ProjectScopeExempt("bound to the route's data source by the handler (404 when it is another source's)")] int Id,
    bool IsVerified) : IRequest, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        ProjectCheck.SharedSource(ProjectResource.DataSource(DataSourceId), ProjectAction.Write)
    ];
}

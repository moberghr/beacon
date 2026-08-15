using Beacon.Core.Authorization;
using Beacon.Core.Data;
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

        var relationship = await context.SchemaRelationships
            .Where(x => x.Id == request.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Schema relationship {request.Id} not found.");

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

public record VerifySchemaRelationshipCommand(int Id, bool IsVerified) : IRequest;

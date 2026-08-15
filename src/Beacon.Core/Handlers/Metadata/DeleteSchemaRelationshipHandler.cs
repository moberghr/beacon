using Beacon.Core.Data;
using Beacon.Core.Services.Metadata;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Metadata;

internal sealed class DeleteSchemaRelationshipHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    ISchemaGraphService schemaGraphService)
    : IRequestHandler<DeleteSchemaRelationshipCommand>
{
    public async Task Handle(DeleteSchemaRelationshipCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var relationship = await context.SchemaRelationships
            .Where(x => x.Id == request.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Schema relationship {request.Id} not found.");

        // Soft delete (§2.14) — the global query filter takes it out of the graph, and re-running sync
        // will not resurrect a foreign-key edge a user deliberately removed.
        relationship.Archive();

        await context.SaveChangesAsync(cancellationToken);

        schemaGraphService.Invalidate(relationship.DataSourceId);
    }
}

public record DeleteSchemaRelationshipCommand(int Id) : IRequest;

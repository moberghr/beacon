using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Metadata;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Metadata;

internal sealed class UpdateSchemaRelationshipHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    ISchemaGraphService schemaGraphService)
    : IRequestHandler<UpdateSchemaRelationshipCommand>
{
    public async Task Handle(UpdateSchemaRelationshipCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var relationship = await context.SchemaRelationships
            .Where(x => x.Id == request.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Schema relationship {request.Id} not found.");

        if (!string.IsNullOrWhiteSpace(request.Label))
        {
            relationship.Label = request.Label;
        }

        relationship.Cardinality = request.Cardinality;

        await context.SaveChangesAsync(cancellationToken);

        schemaGraphService.Invalidate(relationship.DataSourceId);
    }
}

public record UpdateSchemaRelationshipCommand : IRequest
{
    public int Id { get; init; }
    public string? Label { get; init; }
    public SchemaRelationshipCardinality Cardinality { get; init; }
}

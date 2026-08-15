using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Metadata;

internal sealed class GetSchemaRelationshipsHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetSchemaRelationshipsQuery, GetSchemaRelationshipsResult>
{
    public async Task<GetSchemaRelationshipsResult> Handle(GetSchemaRelationshipsQuery request, CancellationToken cancellationToken)
    {
        if (request.DataSourceId <= 0)
        {
            throw new InvalidOperationException("Data source id must be positive.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var relationships = context.SchemaRelationships
            .AsNoTracking()
            .Where(x => x.DataSourceId == request.DataSourceId);

        if (request.Origin != null)
        {
            relationships = relationships.Where(x => x.Origin == request.Origin);
        }

        if (request.VerifiedOnly)
        {
            relationships = relationships.Where(x => x.IsVerified);
        }

        var items = await relationships
            .OrderBy(x => x.SourceSchema)
            .ThenBy(x => x.SourceTable)
            .ThenBy(x => x.SourceColumn)
            .Select(x =>
                new SchemaRelationshipItem
                {
                    Id = x.Id,
                    SourceSchema = x.SourceSchema,
                    SourceTable = x.SourceTable,
                    SourceColumn = x.SourceColumn,
                    TargetSchema = x.TargetSchema,
                    TargetTable = x.TargetTable,
                    TargetColumn = x.TargetColumn,
                    Label = x.Label,
                    Origin = x.Origin,
                    Cardinality = x.Cardinality,
                    Confidence = x.Confidence,
                    IsVerified = x.IsVerified,
                    VerifiedTime = x.VerifiedTime
                })
            .ToListAsync(cancellationToken);

        return new GetSchemaRelationshipsResult(items);
    }
}

public record GetSchemaRelationshipsQuery(
    int DataSourceId,
    SchemaRelationshipOrigin? Origin = null,
    bool VerifiedOnly = false) : IRequest<GetSchemaRelationshipsResult>;

public record GetSchemaRelationshipsResult(IReadOnlyList<SchemaRelationshipItem> Relationships);

public record SchemaRelationshipItem
{
    public int Id { get; init; }
    public required string SourceSchema { get; init; }
    public required string SourceTable { get; init; }
    public required string SourceColumn { get; init; }
    public required string TargetSchema { get; init; }
    public required string TargetTable { get; init; }
    public required string TargetColumn { get; init; }
    public required string Label { get; init; }
    public SchemaRelationshipOrigin Origin { get; init; }
    public SchemaRelationshipCardinality Cardinality { get; init; }
    public double Confidence { get; init; }
    public bool IsVerified { get; init; }
    public DateTime? VerifiedTime { get; init; }
}

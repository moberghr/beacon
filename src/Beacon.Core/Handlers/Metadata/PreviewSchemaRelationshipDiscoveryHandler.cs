using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Metadata;
using MediatR;

namespace Beacon.Core.Handlers.Metadata;

/// <summary>
/// pgGraph's <c>preview_discover</c>: shows what discovery would register without writing anything, so a
/// user can review proposals — especially inferred ones — before they become part of the graph.
/// </summary>
internal sealed class PreviewSchemaRelationshipDiscoveryHandler(ISchemaRelationshipSyncService syncService)
    : IRequestHandler<PreviewSchemaRelationshipDiscoveryQuery, PreviewSchemaRelationshipDiscoveryResult>
{
    public async Task<PreviewSchemaRelationshipDiscoveryResult> Handle(
        PreviewSchemaRelationshipDiscoveryQuery request,
        CancellationToken cancellationToken)
    {
        if (request.DataSourceId <= 0)
        {
            throw new InvalidOperationException("Data source id must be positive.");
        }

        var proposals = await syncService.PreviewAsync(request.DataSourceId, cancellationToken);

        var items = proposals
            .Select(x =>
                new ProposedRelationshipItem
                {
                    SourceSchema = x.SourceSchema,
                    SourceTable = x.SourceTable,
                    SourceColumn = x.SourceColumn,
                    TargetSchema = x.TargetSchema,
                    TargetTable = x.TargetTable,
                    TargetColumn = x.TargetColumn,
                    Label = x.Label,
                    Origin = x.Origin,
                    Cardinality = x.Cardinality,
                    Confidence = x.Confidence
                })
            .ToList();

        return new PreviewSchemaRelationshipDiscoveryResult(items);
    }
}

public record PreviewSchemaRelationshipDiscoveryQuery(int DataSourceId)
    : IRequest<PreviewSchemaRelationshipDiscoveryResult>;

public record PreviewSchemaRelationshipDiscoveryResult(IReadOnlyList<ProposedRelationshipItem> Proposals);

public record ProposedRelationshipItem
{
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
}

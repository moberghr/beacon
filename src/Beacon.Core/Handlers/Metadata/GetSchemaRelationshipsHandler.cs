using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Metadata;

internal sealed class GetSchemaRelationshipsHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetSchemaRelationshipsQuery, PagedList<SchemaRelationshipItem>>
{
    public async Task<PagedList<SchemaRelationshipItem>> Handle(GetSchemaRelationshipsQuery request, CancellationToken cancellationToken)
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

        if (request.VerifiedOnly == true)
        {
            relationships = relationships.Where(x => x.IsVerified);
        }

        return await relationships
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
            .ToPagedListAsync(request, cancellationToken, defaultSort: "sourceSchema,sourceTable,sourceColumn");
    }
}

/// <summary>
/// A data source's relationships, ordered by source schema, table and column unless <c>sort</c> says
/// otherwise. <c>DataSourceId</c> binds from the route.
/// </summary>
public record GetSchemaRelationshipsQuery : ListRequest, IRequest<PagedList<SchemaRelationshipItem>>
{
    public int DataSourceId { get; init; }

    public SchemaRelationshipOrigin? Origin { get; init; }

    public bool? VerifiedOnly { get; init; }
}

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

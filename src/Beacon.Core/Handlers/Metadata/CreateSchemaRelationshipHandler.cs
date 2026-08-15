using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Metadata;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Metadata;

internal sealed class CreateSchemaRelationshipHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    ISchemaGraphService schemaGraphService,
    IBeaconUserContext userContext)
    : IRequestHandler<CreateSchemaRelationshipCommand, CreateSchemaRelationshipResult>
{
    public async Task<CreateSchemaRelationshipResult> Handle(CreateSchemaRelationshipCommand request, CancellationToken cancellationToken)
    {
        if (request.DataSourceId <= 0)
        {
            throw new InvalidOperationException("Data source id must be positive.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dataSourceExists = await context.DataSources
            .Where(x => x.Id == request.DataSourceId)
            .AnyAsync(cancellationToken);
        if (!dataSourceExists)
        {
            throw new InvalidOperationException($"Data source {request.DataSourceId} not found.");
        }

        var duplicate = await context.SchemaRelationships
            .Where(x => x.DataSourceId == request.DataSourceId)
            .Where(x => x.SourceSchema == request.SourceSchema)
            .Where(x => x.SourceTable == request.SourceTable)
            .Where(x => x.SourceColumn == request.SourceColumn)
            .Where(x => x.TargetSchema == request.TargetSchema)
            .Where(x => x.TargetTable == request.TargetTable)
            .Where(x => x.TargetColumn == request.TargetColumn)
            .AnyAsync(cancellationToken);
        if (duplicate)
        {
            throw new InvalidOperationException("A relationship already exists for that column pair.");
        }

        var relationship = new SchemaRelationship
        {
            DataSourceId = request.DataSourceId,
            SourceSchema = request.SourceSchema,
            SourceTable = request.SourceTable,
            SourceColumn = request.SourceColumn,
            TargetSchema = request.TargetSchema,
            TargetTable = request.TargetTable,
            TargetColumn = request.TargetColumn,
            Label = string.IsNullOrWhiteSpace(request.Label) ? request.SourceColumn : request.Label,
            Origin = SchemaRelationshipOrigin.Manual,
            Cardinality = request.Cardinality,
            ConstraintName = null,
            Confidence = 1.0,
            IsVerified = true,
            VerifiedByUserId = ParseUserId(userContext.UserId),
            VerifiedTime = DateTime.UtcNow
        };

        context.SchemaRelationships.Add(relationship);
        await context.SaveChangesAsync(cancellationToken);

        // The cached graph is now stale — leaving it would keep serving pre-edit joins (§6.2).
        schemaGraphService.Invalidate(request.DataSourceId);

        return new CreateSchemaRelationshipResult(relationship.Id);
    }

    private static int? ParseUserId(string? userId) =>
        int.TryParse(userId, out var parsed) ? parsed : null;
}

public record CreateSchemaRelationshipCommand : IRequest<CreateSchemaRelationshipResult>
{
    public int DataSourceId { get; init; }
    public required string SourceSchema { get; init; }
    public required string SourceTable { get; init; }
    public required string SourceColumn { get; init; }
    public required string TargetSchema { get; init; }
    public required string TargetTable { get; init; }
    public required string TargetColumn { get; init; }
    public string? Label { get; init; }
    public SchemaRelationshipCardinality Cardinality { get; init; }
}

public record CreateSchemaRelationshipResult(int Id);

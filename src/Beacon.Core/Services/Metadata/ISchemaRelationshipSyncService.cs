using Beacon.Core.Data.Enums;

namespace Beacon.Core.Services.Metadata;

/// <summary>
/// Keeps <see cref="Data.Entities.Metadata.SchemaRelationship"/> rows in step with extracted metadata:
/// declared foreign keys are reconciled, naming-convention inference proposes edges for sources that
/// declare none, and user-declared edges are left alone.
/// </summary>
public interface ISchemaRelationshipSyncService
{
    /// <summary>
    /// Reconciles foreign-key edges and adds newly inferred ones. Idempotent — re-running against
    /// unchanged metadata adds nothing.
    /// </summary>
    Task<SchemaRelationshipSyncResult> SyncAsync(int dataSourceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes what <see cref="SyncAsync"/> would add without writing anything — pgGraph's
    /// <c>preview_discover</c>. Returns only edges not already registered.
    /// </summary>
    Task<IReadOnlyList<ProposedSchemaRelationship>> PreviewAsync(int dataSourceId, CancellationToken cancellationToken = default);
}

/// <summary>
/// A relationship proposed by discovery, before it is persisted.
/// </summary>
public record ProposedSchemaRelationship(
    string SourceSchema,
    string SourceTable,
    string SourceColumn,
    string TargetSchema,
    string TargetTable,
    string TargetColumn,
    string Label,
    SchemaRelationshipOrigin Origin,
    SchemaRelationshipCardinality Cardinality,
    string? ConstraintName,
    double Confidence);

public record SchemaRelationshipSyncResult(
    int ForeignKeyAdded,
    int ForeignKeyArchived,
    int InferredAdded);

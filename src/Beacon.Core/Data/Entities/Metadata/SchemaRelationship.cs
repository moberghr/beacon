using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities.Metadata;

/// <summary>
/// A registered edge between two tables of a data source. Foreign keys seed these, naming-convention
/// inference proposes more, and users may declare or correct any of them — the graph used for join
/// grounding is built from this table, not from column-level foreign-key fields, so a warehouse with no
/// declared constraints can still have a usable relationship graph.
/// </summary>
public class SchemaRelationship : ArchivableBaseEntity
{
    public int DataSourceId { get; set; }
    public DataSource DataSource { get; set; } = null!;

    public required string SourceSchema { get; set; }
    public required string SourceTable { get; set; }
    public required string SourceColumn { get; set; }

    public required string TargetSchema { get; set; }
    public required string TargetTable { get; set; }
    public required string TargetColumn { get; set; }

    /// <summary>
    /// Human-facing edge name, derived from the source column with an `_id`/`_fk` suffix stripped
    /// (pgGraph's <c>edge_label()</c>): `customer_id` becomes `customer`.
    /// </summary>
    public required string Label { get; set; }

    public SchemaRelationshipOrigin Origin { get; set; }
    public SchemaRelationshipCardinality Cardinality { get; set; }

    /// <summary>
    /// Constraint name for <see cref="SchemaRelationshipOrigin.ForeignKey"/> edges. Edges of one composite
    /// foreign key share this value and must be joined together.
    /// </summary>
    public string? ConstraintName { get; set; }

    /// <summary>1.0 for foreign-key and manual edges; the inference rule's score otherwise.</summary>
    public double Confidence { get; set; }

    /// <summary>
    /// False only for <see cref="SchemaRelationshipOrigin.Inferred"/> edges awaiting review. Unverified
    /// edges still participate in join paths but are rendered to the LLM under an explicit warning.
    /// </summary>
    public bool IsVerified { get; set; }

    public int? VerifiedByUserId { get; set; }
    public DateTime? VerifiedTime { get; set; }
}

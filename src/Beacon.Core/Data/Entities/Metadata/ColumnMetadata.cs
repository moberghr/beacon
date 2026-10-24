using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities.Metadata;

public class ColumnMetadata : BaseEntity
{
    public int DatabaseMetadataId { get; set; }
    public DatabaseMetadata DatabaseMetadata { get; set; } = null!;

    public required string ColumnName { get; set; }
    public required string DataType { get; set; }
    public bool IsNullable { get; set; }
    public bool IsPrimaryKey { get; set; }
    public bool IsForeignKey { get; set; }
    public int OrdinalPosition { get; set; }

    public string? ForeignKeyTable { get; set; }
    public string? ForeignKeyColumn { get; set; }

    /// <summary>
    /// Schema of <see cref="ForeignKeyTable"/>. Null for rows extracted before FK qualification landed,
    /// and for connectors whose catalog does not expose it — consumers fall back to name-only matching
    /// within the source table's own schema when this is null.
    /// </summary>
    public string? ForeignKeySchema { get; set; }

    /// <summary>
    /// Name of the FK constraint this column participates in. Columns of a composite foreign key share
    /// one value, which is the only signal that they must be joined together.
    /// </summary>
    public string? ForeignKeyConstraintName { get; set; }

    /// <summary>
    /// Every foreign key this column takes part in, as JSON (<see cref="Models.Metadata.ColumnForeignKeys"/>), when it takes
    /// part in more than one; the single-target columns above hold the first in constraint-name order. Null when the column
    /// takes part in at most one (the columns above then hold it) and for rows stored before the list was kept.
    /// </summary>
    public string? ForeignKeys { get; set; }

    public string? DefaultValue { get; set; }
    public int? MaxLength { get; set; }
    public string? Description { get; set; }
    public string? SampleValues { get; set; }

    /// <summary>
    /// True when <see cref="SampleValues"/> holds every distinct value of this column (a bounded
    /// DISTINCT probe returned at most 12 rows), false when it is only a 5-row sample.
    /// </summary>
    public bool SampleValuesComplete { get; set; }
}

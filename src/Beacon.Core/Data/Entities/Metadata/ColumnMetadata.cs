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

    public string? DefaultValue { get; set; }
    public int? MaxLength { get; set; }
    public string? Description { get; set; }
    public string? SampleValues { get; set; }
}

using Semantico.Core.Data.Entities.Base;

namespace Semantico.Core.Data.Entities.Metadata;

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
    public string? DefaultValue { get; set; }
    public int? MaxLength { get; set; }
    public string? Description { get; set; }
}

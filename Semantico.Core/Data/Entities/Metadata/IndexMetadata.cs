using Semantico.Core.Data.Entities.Base;

namespace Semantico.Core.Data.Entities.Metadata;

public class IndexMetadata : BaseEntity
{
    public int DatabaseMetadataId { get; set; }
    public DatabaseMetadata DatabaseMetadata { get; set; } = null!;

    public required string IndexName { get; set; }
    public bool IsUnique { get; set; }
    public bool IsPrimaryKey { get; set; }
    public required string[] Columns { get; set; }
}

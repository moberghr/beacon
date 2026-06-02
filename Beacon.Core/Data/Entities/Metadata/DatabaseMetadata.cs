using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities.Metadata;

public class DatabaseMetadata : ArchivableBaseEntity
{
    public int DataSourceId { get; set; }
    public DataSource DataSource { get; set; } = null!;

    public required string SchemaName { get; set; }
    public required string TableName { get; set; }
    public string? TableDescription { get; set; }

    public ICollection<ColumnMetadata> Columns { get; set; } = new List<ColumnMetadata>();
    public ICollection<IndexMetadata> Indexes { get; set; } = new List<IndexMetadata>();

    public DateTime LastRefreshed { get; set; } = DateTime.UtcNow;
}

using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Data.Entities.Projects;

public class SchemaChange : BaseEntity
{
    public int DataSourceId { get; set; }
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
    public SchemaChangeType ChangeType { get; set; }

    public required string SchemaName { get; set; }
    public required string TableName { get; set; }
    public string? ColumnName { get; set; }

    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? Description { get; set; }

    public DataSource DataSource { get; set; } = null!;
}

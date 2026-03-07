using Semantico.Core.Data.Entities.Base;

namespace Semantico.Core.Data.Entities.Projects;

public class SchemaSnapshot : BaseEntity
{
    public int DataSourceId { get; set; }
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
    public required string SchemaJson { get; set; }

    public DataSource DataSource { get; set; } = null!;
}

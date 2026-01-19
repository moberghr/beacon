using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Data.Entities;

public class DataSourceDocumentation : ArchivableBaseEntity
{
    public int DataSourceId { get; set; }
    public string Title { get; set; } = null!;
    public string GeneratedByModel { get; set; } = null!;
    public DateTime GeneratedAt { get; set; }
    public int GeneratedByUserId { get; set; }
    public int? LastModifiedByUserId { get; set; }
    public DocumentationStatus Status { get; set; }
    public int TablesAnalyzed { get; set; }
    public int TokensUsed { get; set; }
    public decimal EstimatedCost { get; set; }
    public string? Metadata { get; set; }
    public string CreatedBy { get; set; } = null!;
    public string ModifiedBy { get; set; } = null!;

    // Navigation properties
    public DataSource DataSource { get; set; } = null!;
    public ICollection<DocumentationSection> Sections { get; set; } = new List<DocumentationSection>();
    public ICollection<DocumentationVersion> Versions { get; set; } = new List<DocumentationVersion>();
}

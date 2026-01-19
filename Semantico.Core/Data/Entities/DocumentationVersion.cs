using Semantico.Core.Data.Entities.Base;

namespace Semantico.Core.Data.Entities;

public class DocumentationVersion : BaseEntity
{
    public int DocumentationId { get; set; }
    public int VersionNumber { get; set; }
    public int CreatedByUserId { get; set; }
    public string? ChangeDescription { get; set; }
    public string SnapshotJson { get; set; } = null!;
    public int SectionsCount { get; set; }
    public int? TokensUsed { get; set; }

    // Navigation properties
    public DataSourceDocumentation Documentation { get; set; } = null!;
}

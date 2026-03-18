using Semantico.Core.Data.Entities.Base;

namespace Semantico.Core.Data.Entities.Projects;

public class ProjectDocumentation : BaseEntity
{
    public int ProjectId { get; set; }
    public string GeneratedByModel { get; set; } = null!;
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public int GeneratedByUserId { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public decimal EstimatedCost { get; set; }
    public int DataSourcesAnalyzed { get; set; }
    public int TablesAnalyzed { get; set; }
    public int CodeReferencesAnalyzed { get; set; }
    public TimeSpan GenerationDuration { get; set; }

    // Navigation
    public Project Project { get; set; } = null!;
    public List<ProjectDocumentationSection> Sections { get; set; } = new();
}
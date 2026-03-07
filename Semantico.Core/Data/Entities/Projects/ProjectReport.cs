using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Data.Entities.Projects;

public class ProjectReport : BaseEntity
{
    public int ProjectId { get; set; }
    public ReportType ReportType { get; set; }
    public ReportFormat ReportFormat { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public string? Content { get; set; }

    public Project Project { get; set; } = null!;
}

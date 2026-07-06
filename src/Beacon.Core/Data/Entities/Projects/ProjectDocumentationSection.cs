using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities.Projects;

public class ProjectDocumentationSection : BaseEntity
{
    public int ProjectDocumentationId { get; set; }
    public ProjectDocSectionType SectionType { get; set; }
    public string Title { get; set; } = null!;
    public string Content { get; set; } = null!;
    public int SortOrder { get; set; }

    // Navigation
    public ProjectDocumentation Documentation { get; set; } = null!;
}
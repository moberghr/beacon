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

    /// <summary>
    /// The <c>Users.Id</c> of the person who last edited the section's content (by hand or by applying a documentation
    /// patch); null for generated content, or for an edit by a caller without a stored Beacon user.
    /// </summary>
    public int? EditedByUserId { get; set; }

    /// <summary>
    /// When a person last edited the section's content; null while it is the generated text. A section with an edit is
    /// curated: regenerating the documentation carries it forward, with these two values, instead of replacing it.
    /// </summary>
    public DateTime? EditedTime { get; set; }

    // Navigation
    public ProjectDocumentation Documentation { get; set; } = null!;
}
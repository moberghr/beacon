using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Data.Entities;

public class DocumentationSection : BaseEntity
{
    public int DocumentationId { get; set; }
    public string? Title { get; set; }
    public SectionType SectionType { get; set; }
    public string? TableName { get; set; }
    public int SortOrder { get; set; }
    public string AiGeneratedContent { get; set; } = null!;
    public string? UserEditedContent { get; set; }
    public bool IsUserEdited { get; set; }
    public ContentFormat ContentFormat { get; set; }
    public string CreatedBy { get; set; } = null!;
    public string ModifiedBy { get; set; } = null!;

    // Navigation properties
    public DataSourceDocumentation Documentation { get; set; } = null!;

    // Helper methods
    public string GetDisplayContent()
    {
        return IsUserEdited ? UserEditedContent! : AiGeneratedContent;
    }

    public string GetDisplayTitle()
    {
        if (!string.IsNullOrEmpty(Title))
            return Title;

        if (SectionType == SectionType.TableDetail && !string.IsNullOrEmpty(TableName))
            return TableName; // No prefix - title is already descriptive (e.g., "Domain: User Management")

        return SectionType.ToString();
    }

    public bool HasBeenModified()
    {
        return IsUserEdited && UserEditedContent != AiGeneratedContent;
    }
}

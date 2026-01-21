using Semantico.Core.Data.Entities.Base;

namespace Semantico.Core.Data.Entities;

public class QueryFolder : ArchivableBaseEntity
{
    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    /// <summary>
    /// Parent folder ID for hierarchical organization. Null means root level.
    /// </summary>
    public int? ParentFolderId { get; set; }

    /// <summary>
    /// Full path from root (e.g., "topfolder/something"). Computed on save.
    /// </summary>
    public string Path { get; set; } = null!;

    /// <summary>
    /// Sort order for displaying folders at the same level
    /// </summary>
    public int SortOrder { get; set; }

    // Navigation properties
    public QueryFolder? ParentFolder { get; set; }

    public List<QueryFolder> ChildFolders { get; set; } = new();

    public List<Query> Queries { get; set; } = new();
}

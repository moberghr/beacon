using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities;

/// <summary>
/// A sentence-window chunk of a <c>ProjectDocumentationSection</c> or of a host-imported
/// <see cref="Projects.ProjectImportedDocument"/>. Stores only text; the vector lives in
/// <see cref="McpEmbedding"/> (OwnerType=DocChunk, OwnerId=this row's Id, ProjectId set).
/// </summary>
public class McpDocChunk : BaseEntity
{
    public int ProjectId { get; set; }

    /// <summary>The source section of a generated-documentation chunk; 0 for an imported-document chunk.</summary>
    public int SourceSectionId { get; set; }

    /// <summary>
    /// Set for a chunk of a host-imported document (then <see cref="SourceSectionId"/> is 0). The section re-index
    /// never touches these rows; the host docs sync owns them.
    /// </summary>
    public int? ImportedDocumentId { get; set; }

    public Projects.ProjectImportedDocument? ImportedDocument { get; set; }

    public required string ChunkText { get; set; }
    public string? ContextualBlurb { get; set; }

    public int SortOrder { get; set; }
}

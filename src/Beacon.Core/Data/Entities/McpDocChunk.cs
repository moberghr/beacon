using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities;

/// <summary>
/// A sentence-window chunk of a <c>ProjectDocumentationSection</c>. Stores only text; the vector
/// lives in <see cref="McpEmbedding"/> (OwnerType=DocChunk, OwnerId=this row's Id, ProjectId set).
/// </summary>
public class McpDocChunk : BaseEntity
{
    public int ProjectId { get; set; }
    public int SourceSectionId { get; set; }

    public required string ChunkText { get; set; }
    public string? ContextualBlurb { get; set; }

    public int SortOrder { get; set; }
}

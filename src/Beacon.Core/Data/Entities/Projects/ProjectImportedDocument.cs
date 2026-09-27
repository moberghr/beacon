using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities.Projects;

/// <summary>
/// A document the host ships with its deployment (markdown / text files or embedded resources declared through
/// <c>BeaconBuilder.ExposeDocs</c>), imported into a project at startup. Keyed by (ProjectId, SourceKey, Path); a
/// file that disappears from the host is archived, and an unchanged file (same <see cref="ContentHash"/>) is left
/// alone. Its chunks live in <see cref="McpDocChunk"/> with <c>ImportedDocumentId</c> set.
/// </summary>
public class ProjectImportedDocument : ArchivableBaseEntity
{
    public const int MaxSourceKeyLength = 300;
    public const int MaxPathLength = 400;

    public int ProjectId { get; set; }

    /// <summary>The host registration the document came from, e.g. <c>docs:dir:docs/wiki</c>.</summary>
    public required string SourceKey { get; set; }

    /// <summary>Forward-slash path relative to the registered directory or resource prefix.</summary>
    public required string Path { get; set; }

    public required string Title { get; set; }

    /// <summary>SHA-256 (hex) of the raw file content, frontmatter included.</summary>
    public required string ContentHash { get; set; }

    /// <summary>Document body with the YAML frontmatter block removed.</summary>
    public required string Content { get; set; }

    /// <summary>Frontmatter as a JSON object, or null when the document has none.</summary>
    public string? FrontmatterJson { get; set; }

    public DateTime ImportedTime { get; set; } = DateTime.UtcNow;

    public Project Project { get; set; } = null!;
}

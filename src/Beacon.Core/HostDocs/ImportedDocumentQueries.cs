using Beacon.Core.Data;

namespace Beacon.Core.HostDocs;

public sealed record ImportedDocumentSummary(int Id, string SourceKey, string Path, string Title, DateTime ImportedTime);

public sealed record ImportedDocumentContent(
    int Id,
    string SourceKey,
    string Path,
    string Title,
    string Content,
    string? FrontmatterJson,
    DateTime ImportedTime);

/// <summary>
/// Read shapes over <c>ProjectImportedDocument</c>, shared by the REST handlers and the MCP <c>get_documentation</c>
/// tool. Every query hard-filters on the caller's project (§1.12) and — through the global soft-delete filter — skips
/// archived documents.
/// </summary>
public static class ImportedDocumentQueries
{
    public static IQueryable<ImportedDocumentSummary> ListForProject(BeaconContext context, int projectId)
    {
        return context.ProjectImportedDocuments
            .Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.Path)
            .ThenBy(x => x.Id)
            .Select(x =>
                new ImportedDocumentSummary(x.Id, x.SourceKey, x.Path, x.Title, x.ImportedTime));
    }

    public static IQueryable<ImportedDocumentContent> ById(BeaconContext context, int projectId, int documentId)
    {
        return context.ProjectImportedDocuments
            .Where(x => x.ProjectId == projectId)
            .Where(x => x.Id == documentId)
            .Select(x =>
                new ImportedDocumentContent(x.Id, x.SourceKey, x.Path, x.Title, x.Content, x.FrontmatterJson, x.ImportedTime));
    }

    /// <summary>
    /// Documents whose path or title equals <paramref name="pathOrTitle"/> (case-insensitive), path matches first.
    /// </summary>
    public static IQueryable<ImportedDocumentContent> ByPathOrTitle(BeaconContext context, int projectId, string pathOrTitle)
    {
        var lower = pathOrTitle.Trim().ToLowerInvariant();

        return context.ProjectImportedDocuments
            .Where(x => x.ProjectId == projectId)
            .Where(x => x.Path.ToLower() == lower || x.Title.ToLower() == lower)
            .OrderBy(x => x.Path.ToLower() == lower ? 0 : 1)
            .ThenBy(x => x.Path)
            .ThenBy(x => x.Id)
            .Select(x =>
                new ImportedDocumentContent(x.Id, x.SourceKey, x.Path, x.Title, x.Content, x.FrontmatterJson, x.ImportedTime));
    }
}

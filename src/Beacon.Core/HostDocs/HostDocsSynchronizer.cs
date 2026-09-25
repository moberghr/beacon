using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Services;
using Beacon.Core.Services.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.HostDocs;

/// <summary>Result of syncing the imported documents of one project.</summary>
internal sealed record HostDocsSyncOutcome(
    int ProjectId,
    int Added,
    int Updated,
    int Archived,
    int Unchanged,
    int Skipped,
    int ChunksWritten);

/// <summary>
/// Upserts every <c>ExposeDocs</c> document into <see cref="ProjectImportedDocument"/>, keyed by
/// (ProjectId, SourceKey, Path). An unchanged file (same content hash) is left alone; a file that disappeared is
/// archived; a new or changed file is re-chunked into <see cref="McpDocChunk"/> so the keyword arm of <c>search</c>
/// finds it with no embedder. When embeddings are available the project's doc-chunk re-index then embeds the new
/// chunks. The project comes from <see cref="IHostProjectResolver"/> (by <c>Project.HostManagedKey</c>, never by name).
/// (SourceKey, Path) is matched case-insensitively, as SQL Server's unique index compares it: a file renamed only by
/// case updates its row, and two files whose paths differ only by case keep the first and skip the other with a
/// warning. Called by the host at startup through <c>SyncBeaconHostAsync</c> — deliberately not a hosted service (§2.15).
/// </summary>
internal sealed class HostDocsSynchronizer(
    IDbContextFactory<BeaconContext> contextFactory,
    IHostProjectResolver projectResolver,
    IEnumerable<HostDocsRegistration> registrations,
    IMcpSettingsProvider settingsProvider,
    IDocChunkIndexingService? docChunkIndexingService,
    string contentRoot,
    ILogger<HostDocsSynchronizer> logger)
{
    public async Task<IReadOnlyList<HostDocsSyncOutcome>> SyncAllAsync(CancellationToken cancellationToken)
    {
        var outcomes = new List<HostDocsSyncOutcome>();
        var byProject = registrations
            .GroupBy(x => x.ProjectName, StringComparer.Ordinal)
            .ToList();

        foreach (var project in byProject)
        {
            outcomes.Add(await SyncProjectAsync(project.Key, project.ToList(), cancellationToken));
        }

        return outcomes;
    }

    public async Task<HostDocsSyncOutcome> SyncProjectAsync(
        string projectName,
        IReadOnlyList<HostDocsRegistration> projectRegistrations,
        CancellationToken cancellationToken)
    {
        var desired = new List<(DiscoveredHostDocument Source, ParsedHostDocument Parsed)>();
        var desiredKeys = new HashSet<(string SourceKey, string Path)>(DocumentKeyComparer.Instance);
        var skipped = 0;
        foreach (var registration in projectRegistrations)
        {
            var discovery = HostDocsDiscovery.Discover(registration, contentRoot);
            foreach (var skip in discovery.Skipped)
            {
                // Relative path and reason only — never the content (§1.11).
                logger.LogWarning("ExposeDocs skipped {Path} from {SourceKey}: {Reason}.", skip.Path, skip.SourceKey, skip.Reason);
            }

            skipped += discovery.Skipped.Count;
            foreach (var document in discovery.Documents)
            {
                if (!desiredKeys.Add((document.SourceKey, document.Path)))
                {
                    // Path only (§1.11). The unique index is case-insensitive on SQL Server, so both cannot be stored.
                    logger.LogWarning("ExposeDocs skipped {Path} from {SourceKey}: another file differs from it only by letter case.", document.Path, document.SourceKey);
                    skipped++;
                    continue;
                }

                desired.Add((document, HostDocumentParser.Parse(document.Path, document.RawContent)));
            }
        }

        var projectId = await projectResolver.EnsureAsync(projectName, null, cancellationToken);
        var settings = await settingsProvider.GetEffectiveSettingsAsync(projectId, cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Archived rows count as present: the unique (ProjectId, SourceKey, Path) index covers them, so a returning
        // file is un-archived rather than inserted.
        var existing = await context.ProjectImportedDocuments
            .IgnoreQueryFilters()
            .Where(x => x.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        // Case-insensitive, like the unique index on SQL Server. Rows that already collide that way (possible on
        // PostgreSQL, whose index is case-sensitive) keep the first match; the others are archived below.
        var existingByKey = new Dictionary<(string SourceKey, string Path), ProjectImportedDocument>(DocumentKeyComparer.Instance);
        foreach (var row in existing.OrderBy(x => x.ArchivedTime == null ? 0 : 1).ThenBy(x => x.Id))
        {
            existingByKey.TryAdd((row.SourceKey, row.Path), row);
        }

        var matchedIds = new HashSet<int>();

        var now = DateTime.UtcNow;
        var added = new List<ProjectImportedDocument>();
        var rechunk = new List<ProjectImportedDocument>();
        var dropChunksFor = new List<int>();
        var unchanged = 0;

        foreach (var (source, parsed) in desired)
        {
            if (!existingByKey.TryGetValue((source.SourceKey, source.Path), out var document))
            {
                var created = new ProjectImportedDocument
                {
                    ProjectId = projectId,
                    SourceKey = source.SourceKey,
                    Path = source.Path,
                    Title = parsed.Title,
                    ContentHash = source.ContentHash,
                    Content = parsed.Body,
                    FrontmatterJson = parsed.FrontmatterJson,
                    ImportedTime = now
                };
                added.Add(created);
                rechunk.Add(created);
                continue;
            }

            matchedIds.Add(document.Id);
            var renamed = document.SourceKey != source.SourceKey || document.Path != source.Path;
            if (document.ArchivedTime == null && document.ContentHash == source.ContentHash && !renamed)
            {
                unchanged++;
                continue;
            }

            document.Unarchive();
            document.SourceKey = source.SourceKey;
            document.Path = source.Path;
            document.Title = parsed.Title;
            document.ContentHash = source.ContentHash;
            document.Content = parsed.Body;
            document.FrontmatterJson = parsed.FrontmatterJson;
            document.ImportedTime = now;
            rechunk.Add(document);
            dropChunksFor.Add(document.Id);
        }

        var archived = existing
            .Where(x => x.ArchivedTime == null)
            .Where(x => !matchedIds.Contains(x.Id))
            .ToList();

        foreach (var document in archived)
        {
            document.Archive();
            dropChunksFor.Add(document.Id);
        }

        if (added.Count == 0 && rechunk.Count == 0 && archived.Count == 0)
        {
            logger.LogInformation(
                "Host docs for project {ProjectId} unchanged: {Unchanged} documents, {Skipped} skipped.",
                projectId,
                unchanged,
                skipped);

            return new HostDocsSyncOutcome(projectId, 0, 0, 0, unchanged, skipped, 0);
        }

        await RemoveChunksAsync(context, projectId, dropChunksFor, cancellationToken);

        var chunks = new List<McpDocChunk>();
        foreach (var document in rechunk)
        {
            var texts = DocumentChunker.Chunk(document.Content, settings.DocChunkWindowSentences, settings.DocChunkOverlapSentences);
            for (var i = 0; i < texts.Count; i++)
            {
                chunks.Add(new McpDocChunk
                {
                    ProjectId = projectId,
                    SourceSectionId = 0,
                    ImportedDocument = document,
                    ChunkText = texts[i],
                    SortOrder = i
                });
            }
        }

        context.ProjectImportedDocuments.AddRange(added);
        context.McpDocChunks.AddRange(chunks);

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Host docs for project {ProjectId} synced: {Added} added, {Updated} updated, {Archived} archived, {Unchanged} unchanged, {Skipped} skipped, {Chunks} chunks written.",
            projectId,
            added.Count,
            rechunk.Count - added.Count,
            archived.Count,
            unchanged,
            skipped,
            chunks.Count);

        await ReindexEmbeddingsAsync(projectId, cancellationToken);

        return new HostDocsSyncOutcome(projectId, added.Count, rechunk.Count - added.Count, archived.Count, unchanged, skipped, chunks.Count);
    }

    private static async Task RemoveChunksAsync(BeaconContext context, int projectId, List<int> documentIds, CancellationToken cancellationToken)
    {
        if (documentIds.Count == 0)
        {
            return;
        }

        var staleChunks = await context.McpDocChunks
            .Where(x => x.ProjectId == projectId)
            .Where(x => x.ImportedDocumentId != null)
            .Where(x => documentIds.Contains(x.ImportedDocumentId!.Value))
            .ToListAsync(cancellationToken);

        if (staleChunks.Count == 0)
        {
            return;
        }

        var staleChunkIds = staleChunks
            .Select(x => x.Id)
            .ToList();

        var staleEmbeddings = await context.McpEmbeddings
            .Where(x => x.ProjectId == projectId)
            .Where(x => x.OwnerType == McpEmbeddingOwnerType.DocChunk)
            .Where(x => staleChunkIds.Contains(x.OwnerId))
            .ToListAsync(cancellationToken);

        context.McpDocChunks.RemoveRange(staleChunks);
        context.McpEmbeddings.RemoveRange(staleEmbeddings);
    }

    // Embeddings are an enrichment: the chunks are already searchable by keyword, and the recurring doc-chunk
    // re-index embeds anything this call could not. A failure here must not fail host startup.
    private async Task ReindexEmbeddingsAsync(int projectId, CancellationToken cancellationToken)
    {
        if (docChunkIndexingService == null)
        {
            return;
        }

        try
        {
            await docChunkIndexingService.ReindexProjectAsync(projectId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Doc-chunk embedding re-index failed for project {ProjectId}; imported documents stay keyword-searchable.", projectId);
        }
    }
}

/// <summary>(SourceKey, Path) compared ignoring case — the comparison SQL Server's unique index applies.</summary>
internal sealed class DocumentKeyComparer : IEqualityComparer<(string SourceKey, string Path)>
{
    public static readonly DocumentKeyComparer Instance = new();

    public bool Equals((string SourceKey, string Path) x, (string SourceKey, string Path) y) =>
        StringComparer.OrdinalIgnoreCase.Equals(x.SourceKey, y.SourceKey)
        && StringComparer.OrdinalIgnoreCase.Equals(x.Path, y.Path);

    public int GetHashCode((string SourceKey, string Path) obj) =>
        HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.SourceKey),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path));
}

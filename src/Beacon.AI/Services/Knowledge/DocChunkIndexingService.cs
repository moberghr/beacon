using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.AI.Services.Embeddings;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Helpers;

namespace Beacon.AI.Services.Knowledge;

/// <summary>
/// Chunks each project's latest documentation into sentence-window <see cref="McpDocChunk"/> rows, embeds
/// each chunk, and upserts the vectors into <see cref="McpEmbedding"/> (OwnerType=DocChunk, ProjectId set,
/// DataSourceId null) so the knowledge-answer path can retrieve the top-K relevant chunks (Tier-3 ⑨) instead
/// of char-truncating the whole documentation. When <c>EnableContextualRetrieval</c> is on and an LLM
/// provider is usable, each chunk is prefixed with a short LLM-generated situating blurb before embedding
/// and the blurb is stored (Tier-3 ⑩, Anthropic contextual retrieval). Only the section documentation text
/// reaches the LLM — never bulk query rows (§1.11). Mirrors <see cref="EmbeddingIndexingService"/>: the
/// interface lives in Core, the impl here, gated by settings, one context per unit of work via
/// <see cref="IDbContextFactory{BeaconContext}"/>, OCE rethrown, all-failed surfaced as an error.
/// </summary>
internal sealed class DocChunkIndexingService(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconEmbeddingService embeddingService,
    IMcpSettingsProvider settingsProvider,
    ILlmProvider llmProvider,
    IEmbeddingVectorColumnWriter vectorWriter,
    ILogger<DocChunkIndexingService> logger) : IDocChunkIndexingService
{
    // Kept identical to EmbeddingIndexingService so all vectors in the shared store share a model/version
    // and a model swap re-indexes everything consistently.
    private const string EmbeddingModelName = "bge-small-en-v1.5";
    private const int CurrentEmbeddingVersion = 1;

    // Contextual blurbs are one or two sentences (~80 tokens); a small ceiling keeps the per-chunk cost low.
    private const int BlurbMaxTokens = 128;

    private const string BlurbSystemPrompt = """
        You situate a documentation chunk within its whole section so it can be retrieved on its own.
        Given the entire documentation section and one chunk taken from it, write a short standalone
        context — one or two sentences, at most about 80 tokens — that explains what the chunk is about
        and where it sits in the section. Answer with ONLY the situating context, no preamble, no quotes.
        """;

    public async Task ReindexAsync(CancellationToken ct)
    {
        if (!embeddingService.IsAvailable)
        {
            logger.LogInformation("Embedding service unavailable; skipping MCP doc-chunk re-index.");
            return;
        }

        var settings = await settingsProvider.GetSettingsAsync(ct);
        if (!settings.EnableSemanticRetrieval)
        {
            logger.LogInformation("Semantic retrieval disabled; skipping MCP doc-chunk re-index.");
            return;
        }

        List<int> projectIds;
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var docProjectIds = await context.ProjectDocumentationSections
                .Select(x => x.Documentation.ProjectId)
                .Distinct()
                .ToListAsync(ct);

            // The glossary (⑪) is refreshed by this same job, so include every project that has glossary
            // terms even when it has no documentation to chunk yet — otherwise its term vectors would never
            // be (re-)embedded or pruned. Filter is deliberately NOT on IsActive: a project whose terms were
            // all deactivated must still be visited so their stale vectors get pruned.
            var glossaryProjectIds = await context.McpGlossaryTerms
                .Select(x => x.ProjectId)
                .Distinct()
                .ToListAsync(ct);

            projectIds = docProjectIds
                .Union(glossaryProjectIds)
                .ToList();
        }

        var failed = 0;
        foreach (var projectId in projectIds)
        {
            try
            {
                await IndexProjectAsync(projectId, settings, ct);
            }
            catch (OperationCanceledException)
            {
                // Job shutdown/cancellation must unwind — do not keep issuing work for later projects.
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogWarning(ex, "Failed to index doc chunks for project {ProjectId}", projectId);
            }
        }

        // A per-project failure must not leave the chunk store silently half-populated behind a green job.
        // If EVERY project failed, throw so the job is marked Failed and investigated (§6.4 — retries off).
        if (failed > 0 && failed == projectIds.Count)
        {
            throw new InvalidOperationException(
                $"MCP doc-chunk re-index failed for all {projectIds.Count} project(s) — chunk store not populated.");
        }

        if (failed > 0)
        {
            logger.LogError("MCP doc-chunk re-index completed with failures: {Failed} of {Total} project(s) failed.", failed, projectIds.Count);
        }
    }

    public async Task ReindexProjectAsync(int projectId, CancellationToken ct)
    {
        if (!embeddingService.IsAvailable)
        {
            return;
        }

        var settings = await settingsProvider.GetSettingsAsync(ct);
        if (!settings.EnableSemanticRetrieval)
        {
            return;
        }

        await IndexProjectAsync(projectId, settings, ct);
    }

    private async Task IndexProjectAsync(int projectId, McpSettingsData settings, CancellationToken ct)
    {
        // Glossary (⑪) is its own unit of work (§5.7) and is independent of whether this project has any
        // documentation to chunk, so it runs first — before the doc-chunk early-return paths below.
        await IndexGlossaryTermsAsync(projectId, ct);

        await using var context = await contextFactory.CreateDbContextAsync(ct);

        // Chunk the LATEST documentation only — that is what the knowledge-answer fallback exports
        // (ExportLatestToMarkdownAsync), so retrieval and fallback stay over the same content and stale
        // generations are never indexed.
        var latestDocId = await context.ProjectDocumentations
            .Where(x => x.ProjectId == projectId)
            .OrderByDescending(x => x.GeneratedAt)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(ct);

        var sections = latestDocId == null
            ? []
            : await context.ProjectDocumentationSections
                .Where(x => x.ProjectDocumentationId == latestDocId.Value)
                .OrderBy(x => x.SortOrder)
                .Select(x =>
                    new SectionSource
                    {
                        Id = x.Id,
                        Content = x.Content
                    })
                .ToListAsync(ct);

        // Deterministic chunk set: (SourceSectionId, chunk index) is the natural key that makes the upsert
        // idempotent — re-running over unchanged content matches the same rows.
        var desired = new List<DesiredChunk>();
        foreach (var section in sections)
        {
            var chunks = DocumentChunker.Chunk(section.Content, settings.DocChunkWindowSentences, settings.DocChunkOverlapSentences);
            for (var i = 0; i < chunks.Count; i++)
            {
                desired.Add(new DesiredChunk(section.Id, i, chunks[i]));
            }
        }

        var existingChunks = await context.McpDocChunks
            .Where(x => x.ProjectId == projectId)
            .ToListAsync(ct);

        var existingEmbeddings = await context.McpEmbeddings
            .Where(x => x.ProjectId == projectId)
            .Where(x => x.OwnerType == McpEmbeddingOwnerType.DocChunk)
            .ToListAsync(ct);

        // Nothing to embed AND nothing to prune → no-op (no idle SaveChanges).
        if (desired.Count == 0 && existingChunks.Count == 0)
        {
            return;
        }

        // Contextual retrieval (⑩): compute a situating blurb per chunk when enabled. A provider error on
        // any chunk falls back to a null blurb (raw chunk embedded) — the chunk is never dropped. Count the
        // blurbs that FAILED (threw) separately from those that legitimately returned blank, so a total
        // provider outage can be surfaced as an Error below rather than hidden behind a green job.
        var blurbs = new string?[desired.Count];
        var blurbFailures = 0;
        if (settings.EnableContextualRetrieval && desired.Count > 0)
        {
            var sectionTextById = sections.ToDictionary(x => x.Id, x => x.Content);
            for (var i = 0; i < desired.Count; i++)
            {
                var chunk = desired[i];
                var blurbResult = await GenerateBlurbAsync(chunk.SourceSectionId, sectionTextById[chunk.SourceSectionId], chunk.Text, ct);
                blurbs[i] = blurbResult.Blurb;
                if (blurbResult.Failed)
                {
                    blurbFailures++;
                }
            }
        }

        // ---- Unit of work A: upsert McpDocChunk rows keyed by (SourceSectionId, chunk index) ----
        var existingByKey = existingChunks.ToDictionary(x => (x.SourceSectionId, x.SortOrder));
        var desiredKeys = desired
            .Select(x => (x.SourceSectionId, x.Index))
            .ToHashSet();

        // Parallel to `desired`: the persisted chunk row (existing or freshly added) for each desired chunk.
        var chunkRows = new McpDocChunk[desired.Count];
        var newChunks = new List<McpDocChunk>();
        for (var i = 0; i < desired.Count; i++)
        {
            var chunk = desired[i];
            if (existingByKey.TryGetValue((chunk.SourceSectionId, chunk.Index), out var row))
            {
                row.ChunkText = chunk.Text;
                row.ContextualBlurb = blurbs[i];
                chunkRows[i] = row;
            }
            else
            {
                var newRow = new McpDocChunk
                {
                    ProjectId = projectId,
                    SourceSectionId = chunk.SourceSectionId,
                    ChunkText = chunk.Text,
                    ContextualBlurb = blurbs[i],
                    SortOrder = chunk.Index
                };
                newChunks.Add(newRow);
                chunkRows[i] = newRow;
            }
        }

        var staleChunks = existingChunks
            .Where(x => !desiredKeys.Contains((x.SourceSectionId, x.SortOrder)))
            .ToList();
        var staleChunkIds = staleChunks
            .Select(x => x.Id)
            .ToHashSet();
        var staleEmbeddings = existingEmbeddings
            .Where(x => staleChunkIds.Contains(x.OwnerId))
            .ToList();

        if (newChunks.Count > 0)
        {
            await context.McpDocChunks.AddRangeAsync(newChunks, ct);
        }

        if (staleChunks.Count > 0)
        {
            context.McpDocChunks.RemoveRange(staleChunks);
        }

        if (staleEmbeddings.Count > 0)
        {
            context.McpEmbeddings.RemoveRange(staleEmbeddings);
        }

        // Save #1: the DB assigns Ids to the new chunks so they can key their embedding rows, and stale
        // chunks + their vectors are pruned in the same save. Two SaveChanges in one background-job unit is
        // acceptable here precisely because the chunk Id is DB-generated yet needed as the embedding OwnerId
        // (same rationale the Tier-2 promotion path documented) — a natural key would otherwise duplicate the
        // OwnerId→row mapping the retrieval seam already relies on.
        await context.SaveChangesAsync(ct);

        // Only pruning happened (no chunks to (re-)embed) — done.
        if (desired.Count == 0)
        {
            logger.LogInformation(
                "Doc-chunk re-index for project {ProjectId}: 0 chunks; {Pruned} stale chunk(s) pruned.",
                projectId, staleChunks.Count);
            return;
        }

        // ---- Unit of work B: embed (blurb + chunk) and upsert McpEmbedding keyed by the chunk Id ----
        var texts = new List<string>(desired.Count);
        for (var i = 0; i < desired.Count; i++)
        {
            texts.Add(BuildEmbeddingText(blurbs[i], desired[i].Text));
        }

        var vectors = await embeddingService.EmbedBatchAsync(texts, ct);
        var dimensions = embeddingService.Dimensions;

        var embeddingByOwnerId = existingEmbeddings
            .Where(x => !staleChunkIds.Contains(x.OwnerId))
            .ToDictionary(x => x.OwnerId);

        var newEmbeddings = new List<McpEmbedding>();
        // (Row, vector) pairs to push into the DB-managed pgvector column after SaveChanges assigns ids.
        var vectorWrites = new List<(McpEmbedding Row, float[] Vector)>();
        for (var i = 0; i < desired.Count; i++)
        {
            var chunkId = chunkRows[i].Id;
            var bytes = EmbeddingCodec.ToBytes(vectors[i]);

            if (embeddingByOwnerId.TryGetValue(chunkId, out var row))
            {
                row.EmbeddingBytes = bytes;
                row.Model = EmbeddingModelName;
                row.Dimensions = dimensions;
                row.EmbeddingVersion = CurrentEmbeddingVersion;
                vectorWrites.Add((row, vectors[i]));
            }
            else
            {
                var newRow = new McpEmbedding
                {
                    // Doc-chunk embeddings are project-scoped; DataSourceId is unused (retrieval filters on project_id).
                    DataSourceId = null,
                    ProjectId = projectId,
                    OwnerType = McpEmbeddingOwnerType.DocChunk,
                    OwnerId = chunkId,
                    EmbeddingBytes = bytes,
                    Model = EmbeddingModelName,
                    Dimensions = dimensions,
                    EmbeddingVersion = CurrentEmbeddingVersion
                };
                newEmbeddings.Add(newRow);
                vectorWrites.Add((newRow, vectors[i]));
            }
        }

        if (newEmbeddings.Count > 0)
        {
            await context.McpEmbeddings.AddRangeAsync(newEmbeddings, ct);
        }

        await context.SaveChangesAsync(ct);

        // Populate the DB-managed pgvector column now that new rows have DB-assigned ids (PostgreSQL only).
        await vectorWriter.WriteAsync(
            context,
            vectorWrites.Select(x => (x.Row.Id, x.Vector)).ToList(),
            ct);

        var withBlurb = blurbs.Count(x => x != null);
        logger.LogInformation(
            "Doc-chunk re-index for project {ProjectId}: {Chunks} chunk(s) ({New} new, {Blurbs} with contextual blurb), {Pruned} stale chunk(s) pruned.",
            projectId, desired.Count, newChunks.Count, withBlurb, staleChunks.Count);

        // Contextual retrieval enabled but EVERY chunk's blurb generation threw (not merely returned blank):
        // the raw chunks were still embedded (never dropped), but a total provider outage would otherwise be
        // invisible — an Information-level withBlurb==0 is indistinguishable from contextual retrieval being
        // off. Surface it as an Error so the failure is visible even though the job itself stays green.
        if (settings.EnableContextualRetrieval && desired.Count > 0 && blurbFailures == desired.Count)
        {
            logger.LogError(
                "Contextual retrieval enabled but blurb generation failed for all {Failed}/{Total} chunk(s) of project {ProjectId} — embedded raw chunks; check the LLM provider.",
                blurbFailures, desired.Count, projectId);
        }
    }

    // Glossary pass (Tier-3 ⑪): embed each ACTIVE glossary term (masked term + synonyms, matching the
    // masked-question retrieval in GetSmartContextForAskAsync) and upsert its vector into McpEmbedding
    // (OwnerType=GlossaryTerm, OwnerId=term id, ProjectId set, DataSourceId unused). Vectors for terms that
    // no longer exist OR were deactivated are pruned. Its own unit of work (§5.7) with a single SaveChanges;
    // term ids are DB-assigned at create time (no two-phase save needed, unlike doc chunks).
    private async Task IndexGlossaryTermsAsync(int projectId, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var terms = await context.McpGlossaryTerms
            .Where(x => x.ProjectId == projectId)
            .Where(x => x.IsActive)
            .Select(x =>
                new GlossaryTermSource
                {
                    Id = x.Id,
                    Term = x.Term,
                    Synonyms = x.Synonyms
                })
            .ToListAsync(ct);

        var existingEmbeddings = await context.McpEmbeddings
            .Where(x => x.ProjectId == projectId)
            .Where(x => x.OwnerType == McpEmbeddingOwnerType.GlossaryTerm)
            .ToListAsync(ct);

        // Nothing to embed AND nothing to prune → no-op (no idle SaveChanges).
        if (terms.Count == 0 && existingEmbeddings.Count == 0)
        {
            return;
        }

        var activeIds = terms
            .Select(x => x.Id)
            .ToHashSet();

        var staleEmbeddings = existingEmbeddings
            .Where(x => !activeIds.Contains(x.OwnerId))
            .ToList();

        var embeddingByOwnerId = existingEmbeddings
            .Where(x => activeIds.Contains(x.OwnerId))
            .ToDictionary(x => x.OwnerId);

        var newEmbeddings = new List<McpEmbedding>();
        // (Row, vector) pairs to push into the DB-managed pgvector column after SaveChanges assigns ids.
        var vectorWrites = new List<(McpEmbedding Row, float[] Vector)>();
        if (terms.Count > 0)
        {
            var texts = terms
                .Select(x => EmbeddingMaskingHelper.Mask(x.Term + " " + (x.Synonyms ?? "")))
                .ToList();

            var vectors = await embeddingService.EmbedBatchAsync(texts, ct);
            var dimensions = embeddingService.Dimensions;

            for (var i = 0; i < terms.Count; i++)
            {
                var bytes = EmbeddingCodec.ToBytes(vectors[i]);
                if (embeddingByOwnerId.TryGetValue(terms[i].Id, out var row))
                {
                    row.EmbeddingBytes = bytes;
                    row.Model = EmbeddingModelName;
                    row.Dimensions = dimensions;
                    row.EmbeddingVersion = CurrentEmbeddingVersion;
                    vectorWrites.Add((row, vectors[i]));
                }
                else
                {
                    var newRow = new McpEmbedding
                    {
                        // Glossary embeddings are project-scoped; DataSourceId is unused (retrieval filters on project_id).
                        DataSourceId = null,
                        ProjectId = projectId,
                        OwnerType = McpEmbeddingOwnerType.GlossaryTerm,
                        OwnerId = terms[i].Id,
                        EmbeddingBytes = bytes,
                        Model = EmbeddingModelName,
                        Dimensions = dimensions,
                        EmbeddingVersion = CurrentEmbeddingVersion
                    };
                    newEmbeddings.Add(newRow);
                    vectorWrites.Add((newRow, vectors[i]));
                }
            }
        }

        if (newEmbeddings.Count > 0)
        {
            await context.McpEmbeddings.AddRangeAsync(newEmbeddings, ct);
        }

        if (staleEmbeddings.Count > 0)
        {
            context.McpEmbeddings.RemoveRange(staleEmbeddings);
        }

        await context.SaveChangesAsync(ct);

        // Populate the DB-managed pgvector column now that new rows have DB-assigned ids (PostgreSQL only).
        await vectorWriter.WriteAsync(
            context,
            vectorWrites.Select(x => (x.Row.Id, x.Vector)).ToList(),
            ct);

        logger.LogInformation(
            "Glossary re-index for project {ProjectId}: {Terms} active term(s) embedded ({New} new), {Pruned} stale vector(s) pruned.",
            projectId, terms.Count, newEmbeddings.Count, staleEmbeddings.Count);
    }

    private async Task<BlurbResult> GenerateBlurbAsync(int sectionId, string sectionText, string chunkText, CancellationToken ct)
    {
        try
        {
            var request = new LlmRequest
            {
                SystemPrompt = BlurbSystemPrompt,
                Messages = [new ChatMessage(ConversationRole.User, BuildBlurbUserMessage(sectionText, chunkText))],
                Temperature = 0.0m,
                MaxTokens = BlurbMaxTokens
            };

            var response = await llmProvider.CompleteAsync(request, ct);
            var blurb = response.Content?.Trim();
            // Legitimately-blank response is NOT a failure — the raw chunk is embedded and the provider is fine.
            return new BlurbResult(string.IsNullOrWhiteSpace(blurb) ? null : blurb, Failed: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort: a provider outage / not-configured error must never drop the chunk. Embed the raw
            // chunk (null blurb) and flag the FAILURE so an all-failed project can be surfaced as an Error
            // (§ Unwanted behaviours — blurb failure → raw chunk).
            logger.LogWarning(ex, "Contextual blurb generation failed for a doc chunk of section {SectionId}; embedding raw chunk.", sectionId);
            return new BlurbResult(null, Failed: true);
        }
    }

    private static string BuildBlurbUserMessage(string sectionText, string chunkText) =>
        $"""
        <document>
        {sectionText}
        </document>

        Here is the chunk we want to situate within the document:
        <chunk>
        {chunkText}
        </chunk>

        Give a short succinct context to situate this chunk within the section for retrieval. Answer only with the succinct context and nothing else.
        """;

    // The blurb is prepended to the chunk before embedding so the vector carries the situating context
    // (Anthropic contextual retrieval). Blank/absent blurb → embed the raw chunk unchanged.
    private static string BuildEmbeddingText(string? blurb, string chunkText) =>
        string.IsNullOrWhiteSpace(blurb) ? chunkText : blurb + "\n\n" + chunkText;

    private sealed class SectionSource
    {
        public int Id { get; init; }
        public string Content { get; init; } = null!;
    }

    private sealed class GlossaryTermSource
    {
        public int Id { get; init; }
        public string Term { get; init; } = null!;
        public string? Synonyms { get; init; }
    }

    private readonly record struct DesiredChunk(int SourceSectionId, int Index, string Text);

    // Distinguishes a blurb that THREW (Failed=true — provider error, raw chunk embedded) from one that
    // legitimately returned blank (Failed=false, Blurb=null), so an all-failed project is surfaced as Error.
    private readonly record struct BlurbResult(string? Blurb, bool Failed);
}

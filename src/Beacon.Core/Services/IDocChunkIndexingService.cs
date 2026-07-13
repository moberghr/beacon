namespace Beacon.Core.Services;

/// <summary>
/// Chunks each project's latest documentation into sentence-window <c>McpDocChunk</c> rows, optionally
/// prepends an LLM-generated contextual blurb (Tier-3 ⑩), embeds each chunk, and upserts the vectors into
/// <c>McpEmbedding</c> (OwnerType=DocChunk, ProjectId set) so the knowledge-answer path can retrieve the
/// top-K relevant chunks instead of char-truncating the whole documentation (Tier-3 ⑨). Mirrors the
/// <see cref="IEmbeddingIndexingService"/> split: the interface lives in Core, the implementation lives in
/// Beacon.AI and is wired at the composition root, so the Core-level Warp job scheduler can trigger a
/// re-index without an AI reference (§2.4). No-op when the embedder is unavailable or semantic retrieval
/// is disabled.
/// </summary>
public interface IDocChunkIndexingService
{
    /// <summary>
    /// Re-chunks and re-embeds every project's latest documentation. Idempotent — an existing chunk for the
    /// same (ProjectId, SourceSectionId, chunk index) is updated in place rather than duplicated, and chunks
    /// no longer produced are pruned along with their embeddings. No-op when the embedder is unavailable or
    /// semantic retrieval is disabled.
    /// </summary>
    Task ReindexAsync(CancellationToken ct);

    /// <summary>
    /// Re-chunks and re-embeds a single project's latest documentation. Same idempotency and availability
    /// semantics as <see cref="ReindexAsync"/>.
    /// </summary>
    Task ReindexProjectAsync(int projectId, CancellationToken ct);
}

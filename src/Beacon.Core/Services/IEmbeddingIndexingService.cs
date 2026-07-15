namespace Beacon.Core.Services;

/// <summary>
/// Populates the <c>McpEmbedding</c> vector store from table/column metadata and validated
/// exemplars so the hybrid-retrieval and semantic few-shot paths have vectors to search.
/// The interface lives in Core (mirroring <see cref="IMcpLearningAggregationService"/>); the
/// implementation lives in Beacon.AI and is wired at the composition root, so the Core-level
/// Hangfire job scheduler can trigger a re-index without an AI reference.
/// </summary>
public interface IEmbeddingIndexingService
{
    /// <summary>
    /// Re-embeds every data source's table/column metadata and validated exemplars.
    /// Idempotent — an existing embedding for the same (DataSourceId, OwnerType, OwnerId) is
    /// overwritten rather than duplicated. No-op when the embedder is unavailable or semantic
    /// retrieval is disabled.
    /// </summary>
    Task ReindexAsync(CancellationToken ct = default);

    /// <summary>
    /// Re-embeds a single data source's metadata and validated exemplars. Same idempotency and
    /// availability semantics as <see cref="ReindexAsync"/>.
    /// </summary>
    Task ReindexDataSourceAsync(int dataSourceId, CancellationToken ct = default);
}

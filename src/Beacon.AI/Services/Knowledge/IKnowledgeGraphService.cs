namespace Beacon.AI.Services.Knowledge;

public interface IKnowledgeGraphService
{
    Task<TableKnowledge> GetTableKnowledgeAsync(int dataSourceId, string schemaName, string tableName, CancellationToken ct = default);
    Task<DataSourceKnowledge> GetDataSourceKnowledgeAsync(int dataSourceId, CancellationToken ct = default);
    Task<List<SearchResult>> SearchAsync(string query, int? dataSourceId = null, int maxResults = 20, CancellationToken ct = default);
    Task<List<SearchResult>> SearchProjectAsync(string query, int projectId, int maxResults = 20, CancellationToken ct = default);
    Task<LineageInfo> GetLineageAsync(int dataSourceId, string schemaName, string tableName, CancellationToken ct = default);
    Task<string> GetContextForLlmAsync(int dataSourceId, string? schemaName = null, string? tableName = null, CancellationToken ct = default);
    Task<string> GetProjectContextForLlmAsync(int projectId, CancellationToken ct = default);
    Task<List<DataSourceKnowledge>> GetProjectDataSourcesAsync(int projectId, CancellationToken ct = default);
    Task<SmartSchemaContext> GetSmartContextForAskAsync(int dataSourceId, string question, CancellationToken ct = default);
    Task<string> GetTablesContextAsync(int dataSourceId, IEnumerable<string> tableNames, CancellationToken ct = default);
    Task<List<LearnedPatternInfo>> GetRelevantPatternsAsync(int dataSourceId, List<string> tableNames, string? question = null, int maxPatterns = 10, int budgetChars = 1500, CancellationToken ct = default);

    /// <summary>
    /// Embeds the RAW question (doc chunks are embedded RAW at index time — no masking) and returns the
    /// top-K project documentation chunks nearest to it (Tier-3 ⑨), in similarity order. Empty when the
    /// embedder is unavailable, semantic retrieval is disabled, nothing is indexed for the project, or a
    /// transient retrieval error occurs — the caller then falls back to char-truncated documentation.
    /// </summary>
    Task<IReadOnlyList<DocChunkHit>> GetRelevantDocChunksAsync(int projectId, string question, int topK, CancellationToken ct = default);
}

/// <summary>
/// A documentation chunk retrieved for the knowledge-answer path. <see cref="ContextualBlurb"/> is the
/// LLM-generated situating sentence (Tier-3 ⑩) when contextual retrieval indexed it, otherwise null.
/// </summary>
public record DocChunkHit(string ChunkText, string? ContextualBlurb);

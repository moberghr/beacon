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
    /// <summary>
    /// Assembles the smart grounding context for an ask against one data source. The caller's
    /// AUTHORIZED <paramref name="projectId"/> is an explicit parameter (never derived from the data
    /// source): a data source can be shared by several projects, and project-scoped grounding
    /// (glossary terms, golden eval cases) must never leak across them.
    /// </summary>
    Task<SmartSchemaContext> GetSmartContextForAskAsync(int dataSourceId, int projectId, string question, CancellationToken ct = default);

    /// <summary>
    /// Builds the schema catalog for a data source — the same
    /// <see cref="SmartSchemaContext.SchemaCatalog"/> shape <see cref="GetSmartContextForAskAsync"/>
    /// computes (keys: lowercase table name AND lowercase "schema.table"; values: lowercase column
    /// names) — WITHOUT the heavy LLM-context assembly. Use for pre-execution column validation.
    /// </summary>
    Task<Dictionary<string, HashSet<string>>> GetSchemaCatalogAsync(int dataSourceId, CancellationToken ct = default);
    Task<string> GetTablesContextAsync(int dataSourceId, IEnumerable<string> tableNames, CancellationToken ct = default);

    /// <summary>
    /// Selects the learned patterns to inject for an ask. The caller's AUTHORIZED
    /// <paramref name="projectId"/> is an explicit parameter for the same reason as on
    /// <see cref="GetSmartContextForAskAsync"/>: a data source can be shared by several projects and
    /// learned patterns are project-scoped — one project's mined lessons (which quote real questions
    /// and SQL) must never be injected into another project's context.
    /// </summary>
    Task<List<LearnedPatternInfo>> GetRelevantPatternsAsync(int dataSourceId, int projectId, List<string> tableNames, string? question = null, int maxPatterns = 10, int budgetChars = 1500, CancellationToken ct = default);

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

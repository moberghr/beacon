namespace Beacon.AI.Services.Knowledge;

public interface IKnowledgeGraphService
{
    /// <summary>
    /// One table's knowledge. Documentation and code references come only from the caller's AUTHORIZED
    /// <paramref name="projectId"/>: a data source can be shared by several projects, and one project's documentation
    /// and repositories must never reach another's caller.
    /// </summary>
    Task<TableKnowledge> GetTableKnowledgeAsync(int dataSourceId, int projectId, string schemaName, string tableName, CancellationToken ct = default);

    /// <summary>
    /// One data source's overview. Its code-reference count and whether documentation exists come only from the
    /// caller's AUTHORIZED <paramref name="projectId"/>, for the same reason as on <see cref="GetTableKnowledgeAsync"/>.
    /// </summary>
    Task<DataSourceKnowledge> GetDataSourceKnowledgeAsync(int dataSourceId, int projectId, CancellationToken ct = default);

    /// <summary>
    /// Tables and columns matching <paramref name="query"/> in the data sources the caller's AUTHORIZED
    /// <paramref name="projectId"/> links: one of them when <paramref name="dataSourceId"/> is set (none when the project
    /// does not link it), otherwise all of them. A data source the project does not link is never searched.
    /// </summary>
    Task<List<SearchResult>> SearchAsync(string query, int projectId, int? dataSourceId = null, int maxResults = 20, CancellationToken ct = default);
    Task<List<SearchResult>> SearchProjectAsync(string query, int projectId, int maxResults = 20, CancellationToken ct = default);

    /// <summary>
    /// One table's lineage. Code references come only from the repositories of the caller's AUTHORIZED
    /// <paramref name="projectId"/>, for the same reason as on <see cref="GetTableKnowledgeAsync"/>.
    /// </summary>
    Task<LineageInfo> GetLineageAsync(int dataSourceId, int projectId, string schemaName, string tableName, CancellationToken ct = default);

    /// <summary>
    /// The LLM context for one data source, or one of its tables. The data source must be linked to the caller's
    /// AUTHORIZED <paramref name="projectId"/> (<see cref="InvalidOperationException"/> otherwise), and a table's
    /// documentation comes from that project only, for the same reason as on <see cref="GetTableKnowledgeAsync"/>.
    /// </summary>
    Task<string> GetContextForLlmAsync(int dataSourceId, int projectId, string? schemaName = null, string? tableName = null, CancellationToken ct = default);
    Task<string> GetProjectContextForLlmAsync(int projectId, CancellationToken ct = default);
    Task<List<DataSourceKnowledge>> GetProjectDataSourcesAsync(int projectId, CancellationToken ct = default);
    /// <summary>
    /// Assembles the smart grounding context for an ask against one data source. The caller's
    /// AUTHORIZED <paramref name="projectId"/> is an explicit parameter (never derived from the data
    /// source): a data source can be shared by several projects, and project-scoped grounding
    /// (glossary terms, golden eval cases) must never leak across them. The data source must be linked to that
    /// project (<see cref="InvalidOperationException"/> otherwise). Grounding reads the data source under the strictest
    /// guardrails of the live projects that link it (value grounding, its probe budget, stored sample values, the PII
    /// patterns). <paramref name="liveValueProbes"/> is true only for a caller allowed to query the project: without it,
    /// value grounding resolves literals from stored sample values alone and runs no probe against the live data.
    /// </summary>
    Task<SmartSchemaContext> GetSmartContextForAskAsync(int dataSourceId, int projectId, string question, bool liveValueProbes, CancellationToken ct = default);

    /// <summary>
    /// Builds the schema catalog for a data source — the same
    /// <see cref="SmartSchemaContext.SchemaCatalog"/> shape <see cref="GetSmartContextForAskAsync"/>
    /// computes (keys: lowercase table name AND lowercase "schema.table"; values: lowercase column
    /// names) — WITHOUT the heavy LLM-context assembly. Use for pre-execution column validation. Empty when the caller's
    /// AUTHORIZED <paramref name="projectId"/> does not link the data source, so every table then fails the check.
    /// </summary>
    Task<Dictionary<string, HashSet<string>>> GetSchemaCatalogAsync(int dataSourceId, int projectId, CancellationToken ct = default);

    /// <summary>
    /// The full schema of the named tables of one data source, for a repair prompt. The data source must be linked to the
    /// caller's AUTHORIZED <paramref name="projectId"/> (<see cref="InvalidOperationException"/> otherwise).
    /// </summary>
    Task<string> GetTablesContextAsync(int dataSourceId, int projectId, IEnumerable<string> tableNames, CancellationToken ct = default);

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

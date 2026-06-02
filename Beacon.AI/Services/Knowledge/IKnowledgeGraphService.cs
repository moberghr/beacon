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
}

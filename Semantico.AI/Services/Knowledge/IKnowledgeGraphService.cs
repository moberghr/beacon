namespace Semantico.AI.Services.Knowledge;

public interface IKnowledgeGraphService
{
    Task<TableKnowledge> GetTableKnowledgeAsync(int dataSourceId, string schemaName, string tableName, CancellationToken ct = default);
    Task<DataSourceKnowledge> GetDataSourceKnowledgeAsync(int dataSourceId, CancellationToken ct = default);
    Task<List<SearchResult>> SearchAsync(string query, int? dataSourceId = null, int maxResults = 20, CancellationToken ct = default);
    Task<LineageInfo> GetLineageAsync(int dataSourceId, string schemaName, string tableName, CancellationToken ct = default);
    Task<string> GetContextForLlmAsync(int dataSourceId, string? schemaName = null, string? tableName = null, CancellationToken ct = default);
}

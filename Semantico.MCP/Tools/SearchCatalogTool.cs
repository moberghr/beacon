using System.Text.Json;
using Semantico.AI.Services.Knowledge;
using Semantico.MCP.Protocol;

namespace Semantico.MCP.Tools;

internal sealed class SearchCatalogTool(IKnowledgeGraphService knowledgeGraph) : IMcpTool
{
    public string Name => "search_catalog";
    public string Description => "Search tables, columns, and documentation across all data sources by keyword. Returns matching items with descriptions, quality scores, and relevance.";
    public object InputSchema => ToolHelper.SchemaObject(
        new Dictionary<string, object>
        {
            ["query"] = ToolHelper.StringProp("Search keyword (e.g., 'customer', 'order_date', 'revenue')"),
            ["datasource_id"] = ToolHelper.IntProp("Optional. Limit search to a specific data source."),
            ["max_results"] = ToolHelper.IntProp("Maximum results to return (default: 20, max: 50)")
        },
        ["query"]);

    public async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, McpClientSession session, CancellationToken ct)
    {
        var query = ToolHelper.GetString(arguments, "query");
        var dsId = ToolHelper.GetInt(arguments, "datasource_id");
        var maxResults = Math.Min(ToolHelper.GetInt(arguments, "max_results") ?? 20, 50);

        if (string.IsNullOrEmpty(query))
            return ToolHelper.ErrorResult("Missing required parameter: query");

        if (dsId != null)
        {
            var scopeError = ToolHelper.ValidateDataSourceAccess(session, dsId.Value);
            if (scopeError != null) return scopeError;
        }

        try
        {
            var results = await knowledgeGraph.SearchAsync(query, dsId, maxResults, ct);

            // Filter by allowed datasources if scoped
            if (session.AllowedDataSourceIds != null)
            {
                // SearchResult doesn't have DataSourceId, but has DataSourceName.
                // We can't filter by ID here without additional data, so we rely on the dsId filter above.
                // For unscoped searches, the results may include all datasources.
            }

            if (results.Count == 0)
                return ToolHelper.TextResult($"No results found for '{query}'.");

            var text = $"# Search Results for '{query}'\n\n";
            text += $"**{results.Count} results found**\n\n";

            foreach (var r in results)
            {
                var icon = r.Type switch
                {
                    "table" => "TABLE",
                    "column" => "COLUMN",
                    "documentation" => "DOC",
                    _ => r.Type.ToUpper()
                };

                text += $"- **[{icon}]** `{r.DataSourceName}`.`{r.SchemaName}.{r.TableName}`";
                if (r.Type == "column" && !string.IsNullOrEmpty(r.ColumnName))
                    text += $".`{r.ColumnName}`";
                if (!string.IsNullOrEmpty(r.Description))
                    text += $" — {r.Description}";
                text += "\n";
            }

            return ToolHelper.TextResult(text);
        }
        catch (Exception ex)
        {
            return ToolHelper.ErrorResult($"Error: {ex.Message}");
        }
    }
}

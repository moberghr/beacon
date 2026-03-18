using System.Text.Json;
using Semantico.AI.Services.Knowledge;
using Semantico.MCP.Protocol;

namespace Semantico.MCP.Tools;

internal sealed class ProjectSearchTool(IKnowledgeGraphService knowledgeGraph) : IMcpTool
{
    public string Name => "search";
    public string Description => "Search tables, columns, and documentation across all data sources in the project by keyword. Returns matching items with descriptions, quality scores, and relevance.";
    public object InputSchema => ToolHelper.SchemaObject(
        new Dictionary<string, object>
        {
            ["query"] = ToolHelper.StringProp("Search keyword (e.g., 'customer', 'order_date', 'revenue')"),
            ["project_id"] = ToolHelper.IntProp("Optional. Specify project if your API key has access to multiple projects."),
            ["max_results"] = ToolHelper.IntProp("Maximum results to return (default: 20, max: 50)")
        },
        ["query"]);

    public async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, McpClientSession session, CancellationToken ct)
    {
        var query = ToolHelper.GetString(arguments, "query");
        var requestedProjectId = ToolHelper.GetInt(arguments, "project_id");
        var maxResults = Math.Min(ToolHelper.GetInt(arguments, "max_results") ?? 20, 50);

        if (string.IsNullOrEmpty(query))
            return ToolHelper.ErrorResult("Missing required parameter: query");

        var resolveError = ToolHelper.ResolveProjectId(session, requestedProjectId, out var projectId);
        if (resolveError != null) return resolveError;

        try
        {
            var results = await knowledgeGraph.SearchProjectAsync(query, projectId, maxResults, ct);

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
                    text += $" -- {r.Description}";
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

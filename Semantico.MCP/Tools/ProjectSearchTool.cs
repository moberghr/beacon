using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Semantico.AI.Services.Knowledge;
using Semantico.MCP.Services;

namespace Semantico.MCP.Tools;

[McpServerToolType]
internal sealed class ProjectSearchTool(
    IKnowledgeGraphService knowledgeGraph,
    IProjectContext projectContext,
    McpProjectContextManager sessionManager,
    McpAuditService auditService)
{
    [McpServerTool(Name = "search")]
    [Description("Search tables, columns, and documentation across all data sources in the project by keyword. Returns matching items with descriptions, quality scores, and relevance.")]
    public async Task<CallToolResult> ExecuteAsync(
        [Description("Search keyword (e.g., 'customer', 'order_date', 'revenue')")]
        string query,
        [Description("Optional. Specify project if your API key has access to multiple projects.")]
        int? project_id = null,
        [Description("Maximum results to return (default: 20, max: 50)")]
        int? max_results = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var maxResults = Math.Min(max_results ?? 20, 50);

        if (string.IsNullOrEmpty(query))
            return ToolHelper.Error("Missing required parameter: query");

        var resolveError = ToolHelper.ResolveProjectId(projectContext, sessionManager, project_id, out var projectId);
        if (resolveError != null) return ToolHelper.Error(resolveError);

        try
        {
            var results = await knowledgeGraph.SearchProjectAsync(query, projectId, maxResults, cancellationToken);

            if (results.Count == 0)
            {
                sw.Stop();
                await auditService.LogToolCallAsync(null, projectContext.UserId, "search",
                    query, null, projectId, (int)sw.ElapsedMilliseconds, 0, null, cancellationToken);
                return ToolHelper.Success($"No results found for '{query}'.");
            }

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

            sw.Stop();
            await auditService.LogToolCallAsync(null, projectContext.UserId, "search",
                query, null, projectId, (int)sw.ElapsedMilliseconds, results.Count, null, cancellationToken);
            return ToolHelper.Success(text);
        }
        catch (Exception ex)
        {
            sw.Stop();
            await auditService.LogToolCallAsync(null, projectContext.UserId, "search",
                query, null, projectId == 0 ? null : projectId, (int)sw.ElapsedMilliseconds, null, ex.Message, CancellationToken.None);
            return ToolHelper.Error(ex.Message);
        }
    }
}

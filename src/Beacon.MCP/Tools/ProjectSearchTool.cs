using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Beacon.AI.Services.Knowledge;
using Beacon.MCP.Services;

namespace Beacon.MCP.Tools;

[McpServerToolType]
internal sealed class ProjectSearchTool(
    IKnowledgeGraphService knowledgeGraph,
    IProjectContext projectContext,
    McpToolCallRecorder recorder)
{
    [McpServerTool(Name = "search", Title = "Search Catalog", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Search tables, columns, and documentation across all data sources in the project by keyword and, when embeddings are enabled, semantic similarity. Host-imported documents appear as IMPORTED_DOC with their path (fetch one with get_documentation document=<path>). Returns matching items with descriptions, ordered by relevance. Page through large result sets with max_results and offset.")]
    public async Task<CallToolResult> ExecuteAsync(
        [Description("Search keyword (e.g., 'customer', 'order_date', 'revenue')")]
        string query,
        [Description("Optional. Specify project if your API key has access to multiple projects.")]
        int? project_id = null,
        [Description("Maximum results to return (default: 20, max: 50)")]
        int? max_results = null,
        [Description("Result offset for paging (default: 0, max: 200). Use with max_results to page through large result sets.")]
        int? offset = null,
        CancellationToken cancellationToken = default)
    {
        // Written by McpAuditCallToolFilter after the tool returns or throws (§1.7).
        recorder.Start("search", projectContext.UserId, query);
        var maxResults = Math.Min(max_results ?? 20, 50);
        var offsetValue = Math.Max(offset ?? 0, 0);

        if (offsetValue > 200)
        {
            const string offsetError = "offset must be between 0 and 200.";
            recorder.Fail(offsetError);
            return ToolHelper.Error(offsetError);
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            const string queryError = "Missing required parameter: query";
            recorder.Fail(queryError);
            return ToolHelper.Error(queryError);
        }

        var resolveError = ToolHelper.ResolveProjectId(projectContext, "search", project_id, out var projectId);
        if (resolveError != null)
        {
            recorder.Fail(resolveError);
            return ToolHelper.Error(resolveError);
        }

        recorder.WithProject(projectId);

        // No McpSignalService call here (audit-only) — see GetContextTool for the full rationale.

        // Over-fetch by one past the requested window so "more available" is detected
        // without a second round-trip; the window is cut in memory below.
        var results = await knowledgeGraph.SearchProjectAsync(query, projectId, offsetValue + maxResults + 1, cancellationToken);

        if (results.Count == 0)
        {
            recorder.Succeed(0);
            return ToolHelper.Success($"No results found for '{query}'.");
        }

        var window = results
            .Skip(offsetValue)
            .Take(maxResults)
            .ToList();

        if (window.Count == 0)
        {
            // Offset points past the end — the fetch came back short of the over-fetch
            // budget, so results.Count is the true total.
            recorder.Succeed(0);
            return ToolHelper.Success($"No results at offset {offsetValue} for '{query}' — only {results.Count} results exist.");
        }

        var text = $"# Search Results for '{query}'\n\n";
        text += offsetValue > 0
            ? $"**Showing {window.Count} results (offset {offsetValue})**\n\n"
            : $"**{window.Count} results found**\n\n";

        foreach (var r in window)
        {
            if (r.Type == "imported_doc")
            {
                // Host-imported document: identified by its path; fetch it with get_documentation(document: path).
                text += $"- **[IMPORTED_DOC]** `{r.DocumentPath}` ({r.DocumentTitle})";
                if (!string.IsNullOrEmpty(r.Description))
                {
                    text += $" -- {r.Description}";
                }

                text += "\n";
                continue;
            }

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

        if (results.Count > offsetValue + maxResults)
        {
            text += $"\n_More results available — repeat with offset={offsetValue + maxResults}._\n";
        }

        recorder.Succeed(window.Count);
        return ToolHelper.Success(text);
    }
}

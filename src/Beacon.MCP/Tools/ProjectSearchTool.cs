using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Beacon.AI.Services.Knowledge;
using Beacon.MCP.Services;

namespace Beacon.MCP.Tools;

[McpServerToolType]
internal sealed class ProjectSearchTool(
    IKnowledgeGraphService knowledgeGraph,
    IProjectContext projectContext,
    McpAuditService auditService,
    ILogger<ProjectSearchTool> logger)
{
    [McpServerTool(Name = "search", Title = "Search Catalog", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Search tables, columns, and documentation across all data sources in the project by keyword and, when embeddings are enabled, semantic similarity. Returns matching items with descriptions, ordered by relevance. Page through large result sets with max_results and offset.")]
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
        var sw = Stopwatch.StartNew();
        var maxResults = Math.Min(max_results ?? 20, 50);
        var offsetValue = Math.Max(offset ?? 0, 0);

        if (offsetValue > 200)
        {
            return ToolHelper.Error("offset must be between 0 and 200.");
        }

        if (string.IsNullOrEmpty(query))
            return ToolHelper.Error("Missing required parameter: query");

        var resolveError = ToolHelper.ResolveProjectId(projectContext, project_id, out var projectId);
        if (resolveError != null) return ToolHelper.Error(resolveError);

        // No McpSignalService call here (audit-only) — see GetContextTool for the full rationale.
        try
        {
            // Over-fetch by one past the requested window so "more available" is detected
            // without a second round-trip; the window is cut in memory below.
            var results = await knowledgeGraph.SearchProjectAsync(query, projectId, offsetValue + maxResults + 1, cancellationToken);

            if (results.Count == 0)
            {
                sw.Stop();
                await auditService.LogToolCallAsync(null, projectContext.UserId, "search",
                    query, null, projectId, (int)sw.ElapsedMilliseconds, 0, null, cancellationToken);
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
                sw.Stop();
                await auditService.LogToolCallAsync(null, projectContext.UserId, "search",
                    query, null, projectId, (int)sw.ElapsedMilliseconds, 0, null, cancellationToken);
                return ToolHelper.Success($"No results at offset {offsetValue} for '{query}' — only {results.Count} results exist.");
            }

            var text = $"# Search Results for '{query}'\n\n";
            text += offsetValue > 0
                ? $"**Showing {window.Count} results (offset {offsetValue})**\n\n"
                : $"**{window.Count} results found**\n\n";

            foreach (var r in window)
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

            if (results.Count > offsetValue + maxResults)
            {
                text += $"\n_More results available — repeat with offset={offsetValue + maxResults}._\n";
            }

            sw.Stop();
            await auditService.LogToolCallAsync(null, projectContext.UserId, "search",
                query, null, projectId, (int)sw.ElapsedMilliseconds, window.Count, null, cancellationToken);
            return ToolHelper.Success(text);
        }
        catch (Exception ex)
        {
            sw.Stop();
            await auditService.LogToolCallAsync(null, projectContext.UserId, "search",
                query, null, projectId == 0 ? null : projectId, (int)sw.ElapsedMilliseconds, null, ex.Message, CancellationToken.None);
            // §1.11 — ex.Message can quote user input; type only here, full detail is in the audit log.
            logger.LogError("MCP tool {Tool} failed with {ExceptionType} (detail in MCP audit log)", "search", ex.GetType().Name);
            return ToolHelper.Error(ToolHelper.CallerSafeMessage(ex, "search"));
        }
    }
}

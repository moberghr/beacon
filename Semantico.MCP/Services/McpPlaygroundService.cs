using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Semantico.Core.Services;
using Semantico.MCP.Tools;

namespace Semantico.MCP.Services;

internal sealed class McpPlaygroundService(IServiceProvider serviceProvider) : IMcpPlaygroundService
{
    public IReadOnlyList<string> ToolNames => ["get_context", "ask", "query", "get_documentation", "search"];

    public async Task<McpPlaygroundResult> ExecuteToolAsync(
        string toolName, Dictionary<string, object?> arguments, int projectId, CancellationToken ct)
    {
        using var scope = serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;

        // Set up project context directly (bypasses IHttpContextAccessor since this is a UI request)
        var context = sp.GetRequiredService<McpProjectContext>();
        context.ActiveProjectId = projectId;
        context.AllowedProjectIds = [projectId];

        try
        {
            var result = toolName switch
            {
                "get_context" => await sp.GetRequiredService<GetContextTool>().ExecuteAsync(
                    project_id: projectId, cancellationToken: ct),

                "ask" => await sp.GetRequiredService<ProjectAskTool>().ExecuteAsync(
                    question: arguments.GetValueOrDefault("question")?.ToString() ?? "",
                    project_id: projectId,
                    execute: arguments.GetValueOrDefault("execute") is bool exec ? exec : true,
                    cancellationToken: ct),

                "query" => await sp.GetRequiredService<ProjectQueryTool>().ExecuteAsync(
                    datasource_name: arguments.GetValueOrDefault("datasource_name")?.ToString(),
                    datasource_id: arguments.GetValueOrDefault("datasource_id") is int dsId ? dsId : null,
                    sql: arguments.GetValueOrDefault("sql")?.ToString(),
                    api_query: arguments.GetValueOrDefault("api_query")?.ToString(),
                    max_rows: arguments.GetValueOrDefault("max_rows") is int mr ? mr : null,
                    project_id: projectId,
                    cancellationToken: ct),

                "get_documentation" => await sp.GetRequiredService<ProjectGetDocumentationTool>().ExecuteAsync(
                    project_id: projectId,
                    datasource_name: arguments.GetValueOrDefault("datasource_name")?.ToString(),
                    table_name: arguments.GetValueOrDefault("table_name")?.ToString(),
                    schema_name: arguments.GetValueOrDefault("schema_name")?.ToString(),
                    cancellationToken: ct),

                "search" => await sp.GetRequiredService<ProjectSearchTool>().ExecuteAsync(
                    query: arguments.GetValueOrDefault("query")?.ToString() ?? "",
                    project_id: projectId,
                    max_results: arguments.GetValueOrDefault("max_results") is int maxR ? maxR : null,
                    cancellationToken: ct),

                _ => ToolHelper.Error($"Unknown tool: {toolName}")
            };

            var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(x => x.Text));

            return new McpPlaygroundResult(text, result.IsError == true);
        }
        catch (Exception ex)
        {
            return new McpPlaygroundResult($"Error: {ex.Message}", true);
        }
    }
}

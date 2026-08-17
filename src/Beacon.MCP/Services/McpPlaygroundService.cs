using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Beacon.Core.Services;
using Beacon.MCP.Tools;

namespace Beacon.MCP.Services;

internal sealed class McpPlaygroundService(IServiceProvider serviceProvider) : IMcpPlaygroundService
{
    public IReadOnlyList<string> ToolNames => ["get_context", "ask", "query", "get_documentation", "search", "feedback", "dry_run", "get_query_context"];

    public async Task<McpPlaygroundResult> ExecuteToolAsync(
        string toolName, Dictionary<string, object?> arguments, int projectId, CancellationToken ct)
    {
        using var scope = serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;

        var httpUser = sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.User;

        // §1.4 — An API-key caller is confined to the projects in its 'allowed_projects' claim.
        // The playground path bypasses the MCP transport's context factory, so the same restriction
        // must be enforced here; otherwise a scoped key could target any project via the request body.
        // Cookie/OIDC sessions carry no such claim and are not project-scoped (role-gated only).
        if (httpUser != null && !IsProjectAllowedForCaller(httpUser, projectId))
        {
            return new McpPlaygroundResult(
                $"Access denied: your API key does not have access to project {projectId}.", true);
        }

        // Set up project context directly (bypasses the MCP transport's context factory since this
        // is a UI request). UserId is still attributed from the authenticated HttpContext so audit /
        // signal entries are not recorded against a null user (§1.7, §9.5).
        var context = sp.GetRequiredService<McpProjectContext>();
        context.ActiveProjectId = projectId;
        context.AllowedProjectIds = [projectId];

        if (httpUser != null)
        {
            var userIdClaim = httpUser.FindFirst(ClaimTypes.NameIdentifier);
            context.UserId = userIdClaim != null && int.TryParse(userIdClaim.Value, out var uid) ? uid : null;

            var apiKeyIdClaim = httpUser.FindFirst("api_key_id");
            context.ApiKeyId = apiKeyIdClaim != null && int.TryParse(apiKeyIdClaim.Value, out var akid) ? akid : null;
        }

        try
        {
            var result = toolName switch
            {
                "get_context" => await sp.GetRequiredService<GetContextTool>().ExecuteAsync(
                    project_id: projectId, cancellationToken: ct),

                "ask" => await sp.GetRequiredService<ProjectAskTool>().ExecuteAsync(
                    question: GetString(arguments, "question") ?? "",
                    project_id: projectId,
                    execute: GetBool(arguments, "execute") ?? true,
                    cancellationToken: ct),

                "query" => await sp.GetRequiredService<ProjectQueryTool>().ExecuteAsync(
                    datasource_name: GetString(arguments, "datasource_name"),
                    datasource_id: GetInt(arguments, "datasource_id"),
                    sql: GetString(arguments, "sql"),
                    api_query: GetString(arguments, "api_query"),
                    max_rows: GetInt(arguments, "max_rows"),
                    project_id: projectId,
                    cancellationToken: ct),

                "get_documentation" => await sp.GetRequiredService<ProjectGetDocumentationTool>().ExecuteAsync(
                    project_id: projectId,
                    datasource_name: GetString(arguments, "datasource_name"),
                    table_name: GetString(arguments, "table_name"),
                    schema_name: GetString(arguments, "schema_name"),
                    response_format: GetString(arguments, "response_format"),
                    cancellationToken: ct),

                "search" => await sp.GetRequiredService<ProjectSearchTool>().ExecuteAsync(
                    query: GetString(arguments, "query") ?? "",
                    project_id: projectId,
                    max_results: GetInt(arguments, "max_results"),
                    offset: GetInt(arguments, "offset"),
                    cancellationToken: ct),

                "dry_run" => await sp.GetRequiredService<DryRunTool>().ExecuteAsync(
                    datasource_name: GetString(arguments, "datasource_name"),
                    datasource_id: GetInt(arguments, "datasource_id"),
                    sql: GetString(arguments, "sql"),
                    project_id: projectId,
                    cancellationToken: ct),

                "get_query_context" => await sp.GetRequiredService<GetQueryContextTool>().ExecuteAsync(
                    question: GetString(arguments, "question") ?? "",
                    datasource_name: GetString(arguments, "datasource_name"),
                    datasource_id: GetInt(arguments, "datasource_id"),
                    project_id: projectId,
                    max_chars: GetInt(arguments, "max_chars"),
                    cancellationToken: ct),

                "feedback" => await sp.GetRequiredService<FeedbackTool>().ExecuteAsync(
                    signal_id: GetInt(arguments, "signal_id") ?? 0,
                    verdict: GetString(arguments, "verdict") ?? "",
                    corrected_sql: GetString(arguments, "corrected_sql"),
                    note: GetString(arguments, "note"),
                    cancellationToken: ct),

                _ => ToolHelper.Error($"Unknown tool: {toolName}")
            };

            var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(x => x.Text));

            return new McpPlaygroundResult(text, result.IsError == true);
        }
        catch (Exception ex)
        {
            // §1.11 — same caller-safe mapping as the MCP transport path; raw ex.Message may quote user input.
            return new McpPlaygroundResult(ToolHelper.CallerSafeMessage(ex, toolName), true);
        }
    }

    // HTTP playground callers arrive via System.Text.Json, so argument values are JsonElement,
    // not CLR primitives — unwrap both shapes so in-process (typed) and HTTP callers behave alike.
    private static string? GetString(Dictionary<string, object?> arguments, string key)
    {
        var value = arguments.GetValueOrDefault(key);
        if (value is JsonElement { ValueKind: JsonValueKind.String } element)
        {
            return element.GetString();
        }

        return value?.ToString();
    }

    private static int? GetInt(Dictionary<string, object?> arguments, string key)
    {
        var value = arguments.GetValueOrDefault(key);
        if (value is int number)
        {
            return number;
        }

        if (value is long longNumber)
        {
            return (int)longNumber;
        }

        if (value is JsonElement { ValueKind: JsonValueKind.Number } element && element.TryGetInt32(out var parsed))
        {
            return parsed;
        }

        return value is string text && int.TryParse(text, out var fromText) ? fromText : null;
    }

    private static bool? GetBool(Dictionary<string, object?> arguments, string key)
    {
        var value = arguments.GetValueOrDefault(key);
        if (value is bool flag)
        {
            return flag;
        }

        if (value is JsonElement { ValueKind: JsonValueKind.True or JsonValueKind.False } element)
        {
            return element.GetBoolean();
        }

        return value is string text && bool.TryParse(text, out var fromText) ? fromText : null;
    }

    // Mirrors ProjectContextFactory's fail-closed reading of the API-key 'allowed_projects' claim.
    // Returns true for callers that are not API keys (cookie/OIDC) — they are not project-scoped.
    private static bool IsProjectAllowedForCaller(ClaimsPrincipal user, int projectId)
    {
        var isApiKey = user.HasClaim("auth_method", "api_key");
        if (!isApiKey)
        {
            return true;
        }

        var allowedProjectsClaim = user.FindFirst("allowed_projects");
        if (allowedProjectsClaim == null || string.IsNullOrEmpty(allowedProjectsClaim.Value))
        {
            return false;
        }

        try
        {
            var allowed = JsonSerializer.Deserialize<List<int>>(allowedProjectsClaim.Value);
            return allowed != null && allowed.Contains(projectId);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

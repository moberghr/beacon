using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Beacon.Core.Authorization;
using Beacon.Core.Mcp;
using Beacon.Core.Services;
using Beacon.MCP.Discovery;
using Beacon.MCP.Tools;

namespace Beacon.MCP.Services;

internal sealed class McpPlaygroundService(IServiceProvider serviceProvider, BeaconMcpOptions options) : IMcpPlaygroundService
{
    internal const string NoRequestMessage = "Access denied: the playground runs only for a signed-in request.";

    // The built-in tools this host serves (BeaconMcpOptions.BuiltInTools), in the catalog's order: one the host left out
    // (the MCP gateway's SQL tools) is not registered with the MCP server, so it is not listed here either.
    public IReadOnlyList<string> ToolNames { get; } = McpToolCatalog.Names
        .Where(x => options.BuiltInTools.Contains(x))
        .ToList();

    public async Task<McpPlaygroundResult> ExecuteToolAsync(
        string toolName, Dictionary<string, object?> arguments, int projectId, CancellationToken ct)
    {
        using var scope = serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;

        // The playground serves a signed-in request only. Without one there is no caller to authorize, and work with no
        // request would otherwise be unrestricted, so it is refused outright.
        var httpUser = sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.User;
        if (httpUser == null)
        {
            await AuditRefusalAsync(sp, toolName, null, NoRequestMessage, ct);
            return new McpPlaygroundResult(NoRequestMessage, true);
        }

        // A built-in tool the host left out (BeaconMcpOptions.BuiltInTools) is not registered with the MCP server, so the
        // playground does not run it either.
        if (!ToolNames.Contains(toolName))
        {
            var unknown = $"Unknown tool: {toolName}";
            var unknownCaller = await sp.GetRequiredService<IBeaconActorAccessor>().GetCurrentAsync(ct);
            await AuditRefusalAsync(sp, toolName, unknownCaller.BeaconUserId, unknown, ct);
            return new McpPlaygroundResult(unknown, true);
        }

        // The caller's project access, as on every other path: the catalog tools read the project, the SQL-carrying
        // tools and feedback work ad hoc in it (Query). A restricted project the caller is not a member of, and a
        // project outside an API key's list, are refused before any tool runs — audited all the same (§1.7).
        var access = await sp.GetRequiredService<IProjectAccessEvaluator>().GetScopeAsync(ct);
        if (!access.Allows(projectId, McpToolProjectAction.Of(toolName)))
        {
            var refusal = $"Access denied: you do not have access to project {projectId}.";
            var refusedCaller = await sp.GetRequiredService<IBeaconActorAccessor>().GetCurrentAsync(ct);
            await AuditRefusalAsync(sp, toolName, refusedCaller.BeaconUserId, refusal, ct);
            return new McpPlaygroundResult(refusal, true);
        }

        // Set up project context directly (bypasses the MCP transport's context filter since this
        // is a UI request). The caller is recorded by its Beacon user (Users.Id), as on the transport, so
        // audit and signal entries name it (§1.7, §9.5) and the caller may rate its own signals.
        var context = sp.GetRequiredService<McpProjectContext>();
        context.ActiveProjectId = projectId;
        context.AllowedProjectIds = [projectId];
        context.QueryProjectIds = access.Allows(projectId, ProjectAction.Query) ? [projectId] : [];

        var actor = await sp.GetRequiredService<IBeaconActorAccessor>().GetCurrentAsync(ct);
        context.UserId = actor.BeaconUserId;

        var apiKeyIdClaim = httpUser.FindFirst("api_key_id");
        context.ApiKeyId = apiKeyIdClaim != null && int.TryParse(apiKeyIdClaim.Value, out var akid) ? akid : null;

        try
        {
            var result = toolName switch
            {
                "get_context" => await sp.GetRequiredService<GetContextTool>().ExecuteAsync(
                    project_id: projectId,
                    format: GetString(arguments, "format"),
                    cancellationToken: ct),

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
                    document: GetString(arguments, "document"),
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

            // Fail-closed audit (Beacon:Mcp:Audit:Required) — this path bypasses the SDK call-tool filter, so it
            // applies the same decision against this scope's audit outcome, after the tool (and its audit) ran.
            if (McpAuditCallToolFilter.ShouldWithhold(sp, result.IsError == true, toolName))
            {
                return new McpPlaygroundResult(McpAuditCallToolFilter.WithheldMessage, true);
            }

            var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(x => x.Text));

            return new McpPlaygroundResult(text, result.IsError == true);
        }
        catch (Exception ex)
        {
            // §1.11 — same caller-safe mapping as the MCP transport path; raw ex.Message may quote user input.
            return new McpPlaygroundResult(ToolHelper.CallerSafeMessage(ex, toolName), true);
        }
    }

    // A playground call refused before any tool ran is a tool invocation all the same: one audit row with the tool name,
    // the caller's Beacon user and the refusal, as the transport's scope filter records one.
    private static async Task AuditRefusalAsync(IServiceProvider sp, string toolName, int? userId, string refusal, CancellationToken ct)
    {
        var audit = sp.GetService<McpAuditService>();
        if (audit == null)
        {
            return;
        }

        await audit.LogToolCallAsync(
            userId: userId,
            tool: toolName,
            parameters: null,
            dataSourceId: null,
            projectId: null,
            executionTimeMs: 0,
            resultRowCount: null,
            errorMessage: refusal,
            ct: ct);
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
}

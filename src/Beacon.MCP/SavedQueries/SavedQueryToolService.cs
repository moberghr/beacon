using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Beacon.Core;
using Beacon.Core.SavedQueries;
using Beacon.Core.Services.Retention;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;

namespace Beacon.MCP.SavedQueries;

/// <summary>
/// Lists and calls the saved-query tools for one MCP request. Visibility is the caller's authorized projects
/// (<see cref="IProjectContext.AllowedProjectIds"/>; none → nothing, fail closed), hard-filtered in
/// <see cref="ISavedQueryToolSource"/> (§1.12). Up to <see cref="SavedQueryToolOptions.NamedToolLimit"/> tools are
/// listed as <c>q_&lt;name&gt;</c>; above it the caller gets <c>search_saved_queries</c> + <c>run_saved_query</c>.
/// Every call is audited as <c>q_&lt;name&gt;</c> on success and failure (§1.7). Saved queries are pre-approved SQL, so
/// they are audit-only: no learning signal is recorded (§9.5 keeps signals for the tools that carry caller SQL).
/// </summary>
internal sealed class SavedQueryToolService(
    ISavedQueryToolSource source,
    ISavedQueryToolExecutor executor,
    IProjectContext projectContext,
    McpAuditService auditService,
    ILogger<SavedQueryToolService> logger,
    BeaconConfiguration? configuration = null)
{
    public const string SearchToolName = "search_saved_queries";
    public const string RunToolName = "run_saved_query";

    internal const int MaxSearchResults = 50;
    internal const int DefaultSearchResults = 10;

    private static readonly JsonElement SearchInputSchema = JsonSerializer.SerializeToElement(new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Keywords matched against saved-query names and descriptions. Empty lists every tool." },
            ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = $"Maximum tools returned (default {DefaultSearchResults}, max {MaxSearchResults})." }
        },
        ["additionalProperties"] = false
    });

    private static readonly JsonElement RunInputSchema = JsonSerializer.SerializeToElement(new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["name"] = new JsonObject { ["type"] = "string", ["description"] = "The saved query name search_saved_queries returned." },
            ["arguments"] = new JsonObject { ["type"] = "object", ["description"] = "Arguments matching the saved query's input schema (include project_id there when it lists one)." }
        },
        ["required"] = new JsonArray("name"),
        ["additionalProperties"] = false
    });

    private int NamedToolLimit => configuration?.SavedQueryTools.NamedToolLimit ?? new SavedQueryToolOptions().NamedToolLimit;

    public static bool Handles(string? toolName) =>
        !string.IsNullOrEmpty(toolName)
        && (toolName is SearchToolName or RunToolName || toolName.StartsWith(SavedQueryToolRules.ToolNamePrefix, StringComparison.Ordinal));

    public async Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var tools = await VisibleToolsAsync(cancellationToken);
        if (tools.Count == 0)
        {
            return [];
        }

        if (tools.Count > NamedToolLimit)
        {
            return [CatalogTool(SearchToolName, "Search saved queries", $"Search the {tools.Count} approved saved queries you can run. Returns each match's name, description and input schema; run one with {RunToolName}.", SearchInputSchema), CatalogTool(RunToolName, "Run a saved query", "Run one approved saved query found with search_saved_queries: pass its name and arguments matching its input schema. Runs read-only; results are row-capped and PII-masked.", RunInputSchema)];
        }

        return tools
            .Select(x => SavedQueryToolSchema.ToProtocolTool(x, x.ProjectIds.Count > 1))
            .ToList();
    }

    public async Task<CallToolResult> CallAsync(string toolName, IDictionary<string, JsonElement>? arguments, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        if (toolName == SearchToolName)
        {
            return await SearchAsync(arguments, sw, cancellationToken);
        }

        var tools = await VisibleToolsAsync(cancellationToken);

        if (toolName == RunToolName)
        {
            var name = arguments != null && arguments.TryGetValue("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;
            var inner = arguments != null && arguments.TryGetValue("arguments", out var innerElement) && innerElement.ValueKind == JsonValueKind.Object
                ? innerElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone())
                : null;

            var target = Find(tools, name);
            if (target == null)
            {
                var error = $"Unknown saved query '{name}'. Use {SearchToolName} to find one.";
                await AuditAsync(RunToolName, arguments, null, null, sw, null, error, null);

                return ToolHelper.Error(error);
            }

            return await InvokeAsync(target, inner, sw, cancellationToken);
        }

        var tool = Find(tools, toolName);
        if (tool == null)
        {
            // Same answer for "does not exist" and "not visible to you": never confirm a query outside the caller's projects.
            var error = $"Unknown saved-query tool '{toolName}'.";
            await AuditAsync(toolName, arguments, null, null, sw, null, error, null);

            return ToolHelper.Error(error);
        }

        return await InvokeAsync(tool, arguments, sw, cancellationToken);
    }

    private async Task<CallToolResult> InvokeAsync(
        SavedQueryToolDefinition tool,
        IDictionary<string, JsonElement>? arguments,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        // The project first: the audit row honours that project's retention settings, so argument content must not be
        // audited before the project is established (an unestablished project audits structurally — see AuditAsync).
        var projectId = 0;
        var projectError = SavedQueryToolSchema.ReadProjectArgument(arguments, out var requestedProjectId);
        projectError ??= ResolveProject(tool, requestedProjectId, out projectId);
        if (projectError != null)
        {
            await AuditAsync(tool.ToolName, arguments, null, null, sw, null, projectError, null);

            return ToolHelper.Error(projectError);
        }

        var argumentError = SavedQueryToolSchema.ConvertArguments(tool, arguments, out var values, out _);
        if (argumentError != null)
        {
            await AuditAsync(tool.ToolName, arguments, projectId, null, sw, null, argumentError, null);

            return ToolHelper.Error(argumentError);
        }

        projectContext.ActiveProjectId = projectId;

        SavedQueryToolExecution execution;
        try
        {
            execution = await executor.ExecuteAsync(tool, projectId, values, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await AuditAsync(tool.ToolName, arguments, projectId, tool.Steps[0].DataSourceId, sw, null, "Cancelled by the caller.", null);
            throw;
        }
        catch (Exception ex)
        {
            await AuditAsync(tool.ToolName, arguments, projectId, tool.Steps[0].DataSourceId, sw, null, ex.Message, null);

            // §1.11 — the exception type only; the detail is in the audit row, which honours the retention lock.
            logger.LogError("Saved-query tool {Tool} (query {QueryId}) failed with {ExceptionType} (detail in MCP audit log)", tool.ToolName, tool.QueryId, ex.GetType().Name);

            return ToolHelper.Error(ToolHelper.CallerSafeMessage(ex, tool.ToolName));
        }

        if (!execution.Success)
        {
            await AuditAsync(tool.ToolName, arguments, projectId, execution.DataSourceId, sw, null, execution.Error, execution.TablesUsed);

            return ToolHelper.Error($"{tool.ToolName} failed: {execution.Error}");
        }

        await AuditAsync(tool.ToolName, arguments, projectId, execution.DataSourceId, sw, execution.Rows.Count, null, execution.TablesUsed);

        return ToolHelper.Success(FormatText(tool, execution), SavedQueryToolResult.BuildStructured(tool, execution));
    }

    private async Task<CallToolResult> SearchAsync(IDictionary<string, JsonElement>? arguments, Stopwatch sw, CancellationToken cancellationToken)
    {
        var tools = await VisibleToolsAsync(cancellationToken);
        var query = arguments != null && arguments.TryGetValue("query", out var queryElement) && queryElement.ValueKind == JsonValueKind.String
            ? queryElement.GetString() ?? string.Empty
            : string.Empty;
        var limit = arguments != null && arguments.TryGetValue("limit", out var limitElement) && limitElement.ValueKind == JsonValueKind.Number && limitElement.TryGetInt32(out var requested)
            ? Math.Clamp(requested, 1, MaxSearchResults)
            : DefaultSearchResults;

        var matches = Search(tools, query, limit);
        var payload = new JsonObject
        {
            ["tools"] = new JsonArray(matches
                .Select(x => (JsonNode?)new JsonObject
                {
                    ["name"] = x.McpToolName,
                    ["title"] = x.Title,
                    ["description"] = x.Description,
                    ["version"] = x.VersionNumber,
                    ["input_schema"] = SavedQueryToolSchema.BuildInputSchema(x, x.ProjectIds.Count > 1)
                })
                .ToArray()),
            ["total_tools"] = tools.Count
        };

        await AuditAsync(SearchToolName, arguments, SingleAllowedProject(), null, sw, matches.Count, null, null);

        return ToolHelper.Success(payload.ToJsonString(), payload);
    }

    internal static IReadOnlyList<SavedQueryToolDefinition> Search(IReadOnlyList<SavedQueryToolDefinition> tools, string query, int limit)
    {
        var terms = query
            .Split([' ', ',', '_', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant())
            .ToList();

        if (terms.Count == 0)
        {
            return tools
                .Take(limit)
                .ToList();
        }

        return tools
            .Select(x => new { Tool = x, Score = terms.Count(y => x.McpToolName.Contains(y, StringComparison.OrdinalIgnoreCase) || x.Title.Contains(y, StringComparison.OrdinalIgnoreCase) || x.Description.Contains(y, StringComparison.OrdinalIgnoreCase)) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Tool.McpToolName, StringComparer.Ordinal)
            .Take(limit)
            .Select(x => x.Tool)
            .ToList();
    }

    private async Task<IReadOnlyList<SavedQueryToolDefinition>> VisibleToolsAsync(CancellationToken cancellationToken)
    {
        var allowed = projectContext.AllowedProjectIds;
        if (allowed == null || allowed.Count == 0)
        {
            return [];
        }

        return await source.GetToolsAsync(allowed, cancellationToken);
    }

    private static SavedQueryToolDefinition? Find(IReadOnlyList<SavedQueryToolDefinition> tools, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        return tools
            .Where(x => x.ToolName == name || x.McpToolName == name)
            .FirstOrDefault();
    }

    private static string? ResolveProject(SavedQueryToolDefinition tool, int? requested, out int projectId)
    {
        projectId = 0;

        if (requested.HasValue)
        {
            if (!tool.ProjectIds.Contains(requested.Value))
            {
                return $"Access denied: {tool.ToolName} is not available in project {requested.Value} for your credentials.";
            }

            projectId = requested.Value;
            return null;
        }

        if (tool.ProjectIds.Count == 1)
        {
            projectId = tool.ProjectIds[0];
            return null;
        }

        return $"{tool.ToolName} is available in several of your projects ({string.Join(", ", tool.ProjectIds)}). Pass {SavedQueryToolRules.ProjectArgumentName} to pick one.";
    }

    private int? SingleAllowedProject()
    {
        var allowed = projectContext.AllowedProjectIds;

        return allowed is { Count: 1 } ? allowed[0] : null;
    }

    private static string FormatText(SavedQueryToolDefinition tool, SavedQueryToolExecution execution)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(tool.Title).Append("\n\n");
        sb.Append("**Tool:** ").Append(tool.ToolName).Append(" (approved version ").Append(tool.VersionNumber).Append(")\n");
        sb.Append("**Rows:** ").Append(execution.Rows.Count);
        if (execution.Truncated)
        {
            sb.Append(" (truncated at the project's row limit of ").Append(execution.MaxRows).Append(')');
        }

        sb.Append("\n\n");
        sb.Append(ToolHelper.FormatResultsAsMarkdown(execution.Rows, execution.MaxRows));

        return sb.ToString();
    }

    private Task AuditAsync(
        string toolName,
        IDictionary<string, JsonElement>? arguments,
        int? projectId,
        int? dataSourceId,
        Stopwatch sw,
        int? rowCount,
        string? error,
        IReadOnlyList<string>? tables)
    {
        sw.Stop();

        // Arguments are content: McpAuditService keeps them only when the project's retention settings allow it, and
        // never logs them (§1.11). Without an established project there is no retention setting to honour, so the row
        // is structural (tool, input size, error class) rather than falling back to the global setting.
        var parameters = arguments == null || arguments.Count == 0 ? null : JsonSerializer.Serialize(arguments);
        if (projectId == null)
        {
            parameters = McpContentRedactor.StructuralAuditParameters(toolName, parameters, tables);
            error = McpContentRedactor.ErrorClassOf(error);
        }

        return auditService.LogToolCallAsync(
            null,
            projectContext.UserId,
            toolName,
            parameters,
            dataSourceId,
            projectId,
            (int)sw.ElapsedMilliseconds,
            rowCount,
            error,
            tables: tables?.ToList(),
            ct: CancellationToken.None);
    }

    private static Tool CatalogTool(string name, string title, string description, JsonElement schema) =>
        new()
        {
            Name = name,
            Title = title,
            Description = description,
            InputSchema = schema,
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = true,
                DestructiveHint = false,
                IdempotentHint = true,
                OpenWorldHint = false
            }
        };
}

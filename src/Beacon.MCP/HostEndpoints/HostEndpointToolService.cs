using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using ModelContextProtocol.Protocol;
using Beacon.Core.Mcp;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;

namespace Beacon.MCP.HostEndpoints;

/// <summary>
/// Lists and calls the host endpoint tools for one MCP request: fails closed unless the caller is a mapped
/// (Entra user or system) caller authorized for the tools' project, builds the host principal, dispatches, maps the
/// response to a tool result, and writes an audit row for every outcome (§1.7).
/// </summary>
internal sealed class HostEndpointToolService(
    HostEndpointToolRegistry registry,
    HostEndpointDispatcher dispatcher,
    IHostEndpointProjectResolver projectResolver,
    IMcpHostPrincipalFactory principalFactory,
    IProjectContext projectContext,
    IHttpContextAccessor httpContextAccessor,
    McpAuditService auditService,
    HostEndpointToolOptions options)
{
    public const string SearchToolName = "search_api";
    public const string CallToolName = "call_api";

    internal const int ErrorExcerptChars = 500;
    internal const int MaxSearchResults = 50;
    internal const int DefaultSearchResults = 10;

    private static readonly JsonElement SearchInputSchema = JsonSerializer.SerializeToElement(new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Keywords matched against tool names and descriptions. Empty lists every tool." },
            ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = $"Maximum tools returned (default {DefaultSearchResults}, max {MaxSearchResults})." }
        },
        ["additionalProperties"] = false
    });

    private static readonly JsonElement CallInputSchema = JsonSerializer.SerializeToElement(new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["name"] = new JsonObject { ["type"] = "string", ["description"] = "The tool name search_api returned." },
            ["arguments"] = new JsonObject { ["type"] = "object", ["description"] = "Arguments matching the tool's input schema." }
        },
        ["required"] = new JsonArray("name"),
        ["additionalProperties"] = false
    });

    public bool Handles(string? toolName)
    {
        if (string.IsNullOrEmpty(toolName) || registry.Tools.Count == 0)
        {
            return false;
        }

        return registry.IsCatalogMode
            ? toolName is SearchToolName or CallToolName
            : toolName.StartsWith(HostEndpointToolDescriptor.ToolNamePrefix, StringComparison.Ordinal) && registry.Find(toolName) != null;
    }

    public async Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken cancellationToken)
    {
        if (registry.Tools.Count == 0 || (await ResolveAccessAsync(cancellationToken)).Error != null)
        {
            return [];
        }

        if (!registry.IsCatalogMode)
        {
            return registry.Tools
                .Select(x => x.ToProtocolTool())
                .ToList();
        }

        return [CatalogTool(SearchToolName, "Search the host application's read-only endpoints", SearchDescription(), SearchInputSchema), CatalogTool(CallToolName, "Call a host application endpoint", "Run one host endpoint tool found with search_api, as your host identity. Pass its name and arguments matching its input schema.", CallInputSchema)];
    }

    public async Task<CallToolResult> CallAsync(string toolName, IDictionary<string, JsonElement>? arguments, CancellationToken cancellationToken)
    {
        if (toolName == SearchToolName)
        {
            return await SearchAsync(arguments, cancellationToken);
        }

        if (toolName == CallToolName)
        {
            var name = arguments != null && arguments.TryGetValue("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;
            var inner = arguments != null && arguments.TryGetValue("arguments", out var innerElement) && innerElement.ValueKind == JsonValueKind.Object
                ? innerElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone())
                : null;

            var target = registry.Find(name);
            if (target == null)
            {
                var sw = Stopwatch.StartNew();
                var access = await ResolveAccessAsync(cancellationToken);
                var error = $"Unknown host endpoint tool '{name}'. Use {SearchToolName} to find one.";
                await AuditAsync(CallToolName, arguments, access.ProjectId, sw, error);

                return ToolHelper.Error(access.Error ?? error);
            }

            return await InvokeAsync(target, inner, cancellationToken);
        }

        var tool = registry.Find(toolName)
            ?? throw new InvalidOperationException($"Unknown host endpoint tool '{toolName}'.");

        return await InvokeAsync(tool, arguments, cancellationToken);
    }

    private async Task<CallToolResult> InvokeAsync(HostEndpointToolDescriptor tool, IDictionary<string, JsonElement>? arguments, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var access = await ResolveAccessAsync(cancellationToken);
        if (access.Error != null)
        {
            await AuditAsync(tool.ToolName, arguments, access.ProjectId, sw, access.Error);

            return ToolHelper.Error(access.Error);
        }

        HostEndpointRequestParts parts;
        try
        {
            parts = HostEndpointRequestBuilder.Build(tool, arguments);
        }
        catch (HostEndpointArgumentException ex)
        {
            await AuditAsync(tool.ToolName, arguments, access.ProjectId, sw, ex.Message);

            return ToolHelper.Error(ex.Message);
        }

        var principal = await principalFactory.CreateAsync(access.Caller!, access.McpPrincipal!, cancellationToken);
        if (principal == null)
        {
            const string refused = "Forbidden: the host application gives this caller no identity for endpoint tools.";
            await AuditAsync(tool.ToolName, arguments, access.ProjectId, sw, refused);

            return ToolHelper.Error(refused);
        }

        HostEndpointDispatchResult result;
        try
        {
            result = await dispatcher.DispatchAsync(tool, parts, principal, access.Caller!, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await AuditAsync(tool.ToolName, arguments, access.ProjectId, sw, "Cancelled by the caller.");
            throw;
        }

        var (toolResult, auditError) = MapResult(tool, result, options.MaxResponseBytes, options.RequestTimeout);
        await AuditAsync(tool.ToolName, arguments, access.ProjectId, sw, auditError);

        return toolResult;
    }

    private async Task<CallToolResult> SearchAsync(IDictionary<string, JsonElement>? arguments, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var access = await ResolveAccessAsync(cancellationToken);
        if (access.Error != null)
        {
            await AuditAsync(SearchToolName, arguments, access.ProjectId, sw, access.Error);

            return ToolHelper.Error(access.Error);
        }

        var query = arguments != null && arguments.TryGetValue("query", out var queryElement) && queryElement.ValueKind == JsonValueKind.String
            ? queryElement.GetString() ?? string.Empty
            : string.Empty;
        var limit = arguments != null && arguments.TryGetValue("limit", out var limitElement) && limitElement.TryGetInt32(out var requested)
            ? Math.Clamp(requested, 1, MaxSearchResults)
            : DefaultSearchResults;

        var matches = Search(registry.Tools, query, limit);
        var payload = new JsonObject
        {
            ["tools"] = new JsonArray(matches
                .Select(x => (JsonNode?)new JsonObject
                {
                    ["name"] = x.Name,
                    ["description"] = x.Description,
                    ["input_schema"] = JsonNode.Parse(x.InputSchema.GetRawText())
                })
                .ToArray()),
            ["total_tools"] = registry.Tools.Count
        };

        await AuditAsync(SearchToolName, arguments, access.ProjectId, sw, null);

        return ToolHelper.Success(payload.ToJsonString(), payload);
    }

    internal static IReadOnlyList<HostEndpointToolDescriptor> Search(IReadOnlyList<HostEndpointToolDescriptor> tools, string query, int limit)
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
            .Select(x => new { Tool = x, Score = terms.Count(y => x.Name.Contains(y, StringComparison.OrdinalIgnoreCase) || x.Description.Contains(y, StringComparison.OrdinalIgnoreCase)) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Tool.Name, StringComparer.Ordinal)
            .Take(limit)
            .Select(x => x.Tool)
            .ToList();
    }

    internal static (CallToolResult Result, string? AuditError) MapResult(
        HostEndpointToolDescriptor tool,
        HostEndpointDispatchResult result,
        int maxResponseBytes,
        TimeSpan timeout)
    {
        switch (result.Outcome)
        {
            case HostEndpointDispatchOutcome.Forbidden:
                return (ToolHelper.Error("Forbidden."), "Forbidden by the host endpoint's authorization policy.");
            case HostEndpointDispatchOutcome.Refused:
                return (ToolHelper.Error("Refused by the host application."), "Refused by the host application (permission).");
            case HostEndpointDispatchOutcome.TimedOut:
                var timedOut = $"The host endpoint timed out after {timeout.TotalSeconds:0.#} s.";
                return (ToolHelper.Error(timedOut), timedOut);
            case HostEndpointDispatchOutcome.ResponseTooLarge:
                var tooLarge = $"The host endpoint's response exceeded the {maxResponseBytes} byte limit; narrow the request.";
                return (ToolHelper.Error(tooLarge), "Response exceeded the size limit.");
            case HostEndpointDispatchOutcome.Failed:
                return (ToolHelper.Error($"The host endpoint behind {tool.ToolName} failed with an unexpected error."), "Execution failed with an unexpected error.");
        }

        var status = result.StatusCode;
        var reason = ReasonPhrases.GetReasonPhrase(status);
        var isJson = IsJson(result.ContentType);
        var isText = isJson || IsText(result.ContentType);

        if (status is < 200 or > 299)
        {
            var message = new StringBuilder($"The host endpoint returned HTTP {status} {reason}.".Replace(" .", "."));
            if (isText && result.Body.Length > 0)
            {
                var body = Encoding.UTF8.GetString(result.Body);
                message.Append('\n').Append(body.Length > ErrorExcerptChars ? body[..ErrorExcerptChars] + "…" : body);
            }

            return (ToolHelper.Error(message.ToString()), $"HTTP {status} {reason}".TrimEnd());
        }

        if (result.Body.Length == 0)
        {
            return (ToolHelper.Success($"HTTP {status} {reason}: no content.".Replace(" :", ":")), null);
        }

        var text = Encoding.UTF8.GetString(result.Body);
        if (!isText && result.ContentType == null && TryParseJson(text, out _))
        {
            isJson = true;
            isText = true;
        }

        if (!isText)
        {
            var unsupported = $"The host endpoint returned '{result.ContentType ?? "no content type"}'; only JSON and text responses are supported.";
            return (ToolHelper.Error(unsupported), "Unsupported response content type (validation).");
        }

        if (!isJson || !TryParseJson(text, out var node))
        {
            return (ToolHelper.Success(text), null);
        }

        // MCP structured content is an object: a top-level array is wrapped as { "items": [...] }.
        JsonNode? structured = node switch
        {
            JsonObject => node,
            JsonArray => new JsonObject { ["items"] = node },
            _ => null
        };

        return (ToolHelper.Success(text, structured), null);
    }

    private static bool IsJson(string? contentType)
    {
        var mediaType = contentType?.Split(';', 2)[0].Trim();

        return mediaType != null
            && (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("text/json", StringComparison.OrdinalIgnoreCase)
                || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsText(string? contentType) =>
        contentType != null && contentType.TrimStart().StartsWith("text/", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseJson(string text, out JsonNode? node)
    {
        try
        {
            node = JsonNode.Parse(text);

            return true;
        }
        catch (JsonException)
        {
            node = null;

            return false;
        }
    }

    private async Task<HostEndpointAccess> ResolveAccessAsync(CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext?.Items[typeof(McpCaller)] is not McpCaller caller)
        {
            return new HostEndpointAccess(null, null, null, "Forbidden: host endpoint tools need a mapped Entra user or system caller; this credential cannot call them.");
        }

        var projectId = await projectResolver.ResolveAsync(cancellationToken);
        if (projectId == null)
        {
            return new HostEndpointAccess(null, null, null, $"Host endpoint tools are unavailable: project '{options.ProjectName}' does not exist in Beacon.");
        }

        var allowedByCaller = caller.AllowedProjectIds.Contains(projectId.Value);
        var allowedByContext = projectContext.AllowedProjectIds?.Contains(projectId.Value) == true;
        if (!allowedByCaller || !allowedByContext)
        {
            return new HostEndpointAccess(projectId, null, null, $"Access denied: you do not have access to project {projectId.Value}.");
        }

        projectContext.ActiveProjectId = projectId.Value;

        return new HostEndpointAccess(projectId, caller, httpContext.User, null);
    }

    private Task AuditAsync(string toolName, IDictionary<string, JsonElement>? arguments, int? projectId, Stopwatch sw, string? error)
    {
        sw.Stop();

        // The arguments are content: McpAuditService keeps them only when the project's retention settings allow it,
        // and never logs them (§1.11).
        var parameters = arguments == null || arguments.Count == 0 ? null : JsonSerializer.Serialize(arguments);

        return auditService.LogToolCallAsync(
            null,
            projectContext.UserId,
            toolName,
            parameters,
            null,
            projectId,
            (int)sw.ElapsedMilliseconds,
            null,
            error,
            ct: CancellationToken.None);
    }

    private string SearchDescription() =>
        $"Search the {registry.Tools.Count} read-only endpoints of the host application (project '{options.ProjectName}') exposed as tools. " +
        $"Returns each match's name, description and input schema; run one with {CallToolName}.";

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

    private sealed record HostEndpointAccess(int? ProjectId, McpCaller? Caller, System.Security.Claims.ClaimsPrincipal? McpPrincipal, string? Error);
}

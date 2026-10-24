using Microsoft.Extensions.Logging;
using System.Security.Claims;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Beacon.Core.Authorization;
using Beacon.Core.HostEndpoints;
using Beacon.Core.Mcp;
using Beacon.MCP.DynamicTools;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;

namespace Beacon.MCP.HostEndpoints;

/// <summary>
/// Lists and calls the host endpoint tools for one MCP request: fails closed unless the caller is a mapped
/// (Entra user or system) caller configured for the tools' project and allowed to read it by its project access
/// (<see cref="IProjectAccessEvaluator"/>), builds the host principal, dispatches, maps the response to a tool result,
/// and writes an audit row for every outcome (§1.7). Write tools (<see cref="HostEndpointToolOptions.AllowWriteTools"/>)
/// are listed to and run for user callers only.
/// </summary>
internal sealed class HostEndpointToolService(
    HostEndpointToolRegistry registry,
    HostEndpointDispatcher dispatcher,
    HostEndpointCallLimiter callLimiter,
    IHostEndpointProjectResolver projectResolver,
    IMcpHostPrincipalFactory principalFactory,
    IProjectContext projectContext,
    IProjectAccessEvaluator projectAccess,
    IHttpContextAccessor httpContextAccessor,
    McpAuditService auditService,
    HostEndpointToolOptions options,
    ILogger<HostEndpointToolService> logger) : IHostEndpointToolOverview, IHostEndpointToolCatalog
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
        if (registry.Tools.Count == 0)
        {
            return [];
        }

        var access = await ResolveAccessAsync(cancellationToken);
        if (access.Error != null)
        {
            return [];
        }

        var visible = VisibleTools(access.Caller!);
        if (!registry.IsCatalogMode)
        {
            return visible
                .Select(x => x.ToProtocolTool())
                .ToList();
        }

        var writes = visible
            .Where(x => !x.ReadOnly)
            .ToList();

        return
        [
            McpCatalogTool.Create(SearchToolName, writes.Count == 0 ? "Search the host application's read-only endpoints" : "Search the host application's endpoints", SearchDescription(visible.Count, writes.Count), SearchInputSchema, McpCatalogTool.ReadOnlyAnnotations()),
            McpCatalogTool.Create(CallToolName, "Call a host application endpoint", CallDescription(writes.Count), CallInputSchema, CallAnnotations(writes))
        ];
    }

    public async Task<HostEndpointToolListing?> ListForProjectAsync(int projectId, CancellationToken cancellationToken)
    {
        if (registry.Tools.Count == 0 || await projectResolver.ResolveAsync(cancellationToken) != projectId)
        {
            return null;
        }

        var access = await ResolveAccessAsync(cancellationToken);
        if (access.Error != null)
        {
            return null;
        }

        var tools = VisibleTools(access.Caller!)
            .Select(x => new HostEndpointToolSummary(x.Name, x.ToolName, x.FirstDescriptionLine()) { ReadOnly = x.ReadOnly })
            .ToList();

        return tools.Count == 0 ? null : new HostEndpointToolListing(tools, registry.IsCatalogMode);
    }

    // The project brief lists what get_context lists: the tools this caller's tools/list offers.
    public Task<HostEndpointToolListing?> GetForProjectAsync(int projectId, CancellationToken cancellationToken) =>
        ListForProjectAsync(projectId, cancellationToken);

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
        var auditTool = tool.AuditToolName;
        var access = await ResolveAccessAsync(cancellationToken);
        if (access.Error != null)
        {
            await AuditAsync(auditTool, arguments, access.ProjectId, sw, access.Error);

            return ToolHelper.Error(access.Error);
        }

        if (!IsVisibleTo(tool, access.Caller!))
        {
            var refused = $"Forbidden: {tool.ToolName} changes data in the host application, and write tools run only for a signed-in user; a system caller cannot call them.";
            await AuditAsync(auditTool, arguments, access.ProjectId, sw, refused);

            return ToolHelper.Error(refused);
        }

        var principal = await principalFactory.CreateAsync(access.Caller!, access.McpPrincipal!, cancellationToken);
        if (principal == null)
        {
            const string refused = "Forbidden: the host application gives this caller no identity for endpoint tools.";
            await AuditAsync(auditTool, arguments, access.ProjectId, sw, refused);

            return ToolHelper.Error(refused);
        }

        HostEndpointRequestParts parts;
        try
        {
            parts = HostEndpointRequestBuilder.Build(tool, arguments, CallerValues(tool, principal));
        }
        catch (HostEndpointArgumentException ex)
        {
            await AuditAsync(auditTool, arguments, access.ProjectId, sw, ex.Message);

            return ToolHelper.Error(ex.Message);
        }

        // The dispatcher frees the slot when the endpoint has ended, which can be after a timed-out call has returned.
        // The slot is taken after the cheap-to-refuse steps above; moving it earlier would need a release on each of them.
        HostEndpointDispatchResult result;
        try
        {
            var slot = await callLimiter.AcquireAsync("caller:" + access.Caller!.SubjectHash, cancellationToken);
            if (slot == null)
            {
                await AuditAsync(auditTool, arguments, access.ProjectId, sw, HostEndpointCallLimiter.RefusalMessage);

                return ToolHelper.Error(HostEndpointCallLimiter.RefusalMessage);
            }

            result = await dispatcher.DispatchAsync(tool, parts, principal, access.Caller!, slot, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await AuditAsync(auditTool, arguments, access.ProjectId, sw, "Cancelled by the caller.");
            throw;
        }
        catch (Exception ex)
        {
            // §1.11 — the type is enough; the message can quote arguments or row data.
            logger.LogWarning("Host endpoint tool {Tool} dispatch failed with {ExceptionType}", tool.ToolName, ex.GetType().Name);
            const string failed = "The host endpoint call failed with an unexpected error.";
            await AuditAsync(auditTool, arguments, access.ProjectId, sw, failed);

            return ToolHelper.Error(failed);
        }

        var (toolResult, auditError) = MapResult(tool, result, options.MaxResponseBytes, options.RequestTimeout);
        await AuditAsync(auditTool, arguments, access.ProjectId, sw, auditError);

        return toolResult;
    }

    // The values the host principal carries for the tool's caller-bound arguments; one it lacks is left out, and the
    // request builder refuses the call for it.
    private Dictionary<string, string> CallerValues(HostEndpointToolDescriptor tool, ClaimsPrincipal principal)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in tool.CallerBindings)
        {
            if (options.CallerBindings.TryGetValue(binding.Name, out var read) && read(principal) is { Length: > 0 } value)
            {
                values[binding.Name] = value;
            }
        }

        return values;
    }

    /// <summary>
    /// Write tools are for user callers only: a system identity (a routine, a pipeline) acting with standing host
    /// permissions has no person who asked for the change and no client prompting one to confirm it.
    /// </summary>
    internal static bool IsVisibleTo(HostEndpointToolDescriptor tool, McpCaller caller) =>
        tool.ReadOnly || caller.Kind == McpCallerKind.User;

    private List<HostEndpointToolDescriptor> VisibleTools(McpCaller caller) =>
        registry.Tools
            .Where(x => IsVisibleTo(x, caller))
            .ToList();

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

        var visible = VisibleTools(access.Caller!);
        var matches = Search(visible, query, limit);
        var payload = new JsonObject
        {
            ["tools"] = new JsonArray(matches
                .Select(x => (JsonNode?)SearchResult(x))
                .ToArray()),
            ["total_tools"] = visible.Count
        };

        await AuditAsync(SearchToolName, arguments, access.ProjectId, sw, null);

        return ToolHelper.Success(payload.ToJsonString(), payload);
    }

    private static JsonObject SearchResult(HostEndpointToolDescriptor tool)
    {
        var result = new JsonObject
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["read_only"] = tool.ReadOnly
        };

        if (!tool.ReadOnly)
        {
            result["destructive"] = tool.Destructive;
            result["idempotent"] = tool.Idempotent;
        }

        result["input_schema"] = JsonNode.Parse(tool.InputSchema.GetRawText());

        return result;
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

        // The caller's configured projects, and its project access: a user caller reads the project only as its user
        // may (a member of a restricted project), a system caller where it is configured.
        var allowedByCaller = caller.AllowedProjectIds.Contains(projectId.Value);
        var allowedByAccess = allowedByCaller
            && (await projectAccess.GetScopeAsync(cancellationToken)).Allows(projectId.Value, ProjectAction.Read);
        if (!allowedByAccess)
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

    private string SearchDescription(int total, int writes) =>
        (writes == 0
            ? $"Search the {total} read-only endpoints of the host application (project '{options.ProjectName}') exposed as tools. "
            : $"Search the {total} endpoints of the host application (project '{options.ProjectName}') exposed as tools; {writes} of them change data (read_only: false). ") +
        $"Returns each match's name, description, read_only flag and input schema; run one with {CallToolName}.";

    private static string CallDescription(int writes) =>
        writes == 0
            ? $"Run one host endpoint tool found with {SearchToolName}, as your host identity. Pass its name and arguments matching its input schema."
            : $"Run one host endpoint tool found with {SearchToolName}, as your host identity. Pass its name and arguments matching its input schema. " +
              "A tool with read_only: false changes data in the host application: call it only when the user asked for that change.";

    /// <summary>
    /// <c>call_api</c> can reach every visible tool, so its hints are the most permissive of them: once one write tool
    /// is reachable it is not read-only (clients then ask before each call), and destructive when any write tool is.
    /// </summary>
    private static ToolAnnotations CallAnnotations(IReadOnlyList<HostEndpointToolDescriptor> writes) =>
        writes.Count == 0
            ? McpCatalogTool.ReadOnlyAnnotations()
            : new ToolAnnotations
            {
                ReadOnlyHint = false,
                DestructiveHint = writes.Any(x => x.Destructive),
                IdempotentHint = false,
                OpenWorldHint = false
            };

    private sealed record HostEndpointAccess(int? ProjectId, McpCaller? Caller, System.Security.Claims.ClaimsPrincipal? McpPrincipal, string? Error);
}

/// <summary>
/// The host endpoint tools as a dynamic tool source (registered by <c>AddHostEndpointTools</c>, after
/// <c>AddBeaconMcp</c>, so listed after the saved-query tools). Its names are known once the host's endpoints are
/// discovered: it claims exactly the names it serves, its <c>api_</c> tools or, in catalog mode, <c>search_api</c> and
/// <c>call_api</c>, and nothing when no endpoint is marked.
/// </summary>
internal sealed class HostEndpointDynamicToolSource(HostEndpointToolRegistry registry) : IMcpDynamicToolSource
{
    private const string SourceName = "the host endpoint tools";

    public McpDynamicToolNames ClaimedNames(IServiceProvider services)
    {
        if (registry.Tools.Count == 0)
        {
            return McpDynamicToolNames.None(SourceName);
        }

        return registry.IsCatalogMode
            ? new(SourceName, [HostEndpointToolService.SearchToolName, HostEndpointToolService.CallToolName], [])
            : new(SourceName, registry.Tools.Select(x => x.ToolName).ToList(), []);
    }

    public bool Handles(IServiceProvider requestServices, string toolName) =>
        requestServices.GetService<HostEndpointToolService>()?.Handles(toolName) == true;

    public async Task<IReadOnlyList<Tool>> ListToolsAsync(IServiceProvider requestServices, CancellationToken cancellationToken)
    {
        var service = requestServices.GetService<HostEndpointToolService>();

        return service == null ? [] : await service.ListToolsAsync(cancellationToken);
    }

    public Task<CallToolResult> CallAsync(IServiceProvider requestServices, string toolName, IDictionary<string, JsonElement>? arguments, CancellationToken cancellationToken) =>
        requestServices.GetRequiredService<HostEndpointToolService>().CallAsync(toolName, arguments, cancellationToken);
}

/// <summary>
/// The host endpoint tools the current MCP caller sees in a project — the ones its <c>tools/list</c> offers — for
/// <c>get_context</c>. Registered by <c>AddHostEndpointTools</c>; absent otherwise.
/// </summary>
internal interface IHostEndpointToolOverview
{
    /// <summary>The caller's tools in <paramref name="projectId"/>, or null when it has none there.</summary>
    Task<HostEndpointToolListing?> ListForProjectAsync(int projectId, CancellationToken cancellationToken);
}

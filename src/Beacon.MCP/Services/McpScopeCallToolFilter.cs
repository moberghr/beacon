using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Beacon.Core.Authorization;
using Beacon.Core.Mcp;

namespace Beacon.MCP.Services;

/// <summary>
/// Scope enforcement inside the MCP layer (§1.4). The route policy of <see cref="BeaconMcpEndpointRouteBuilderExtensions.MapBeaconMcp"/>
/// already refuses a scoped caller (API key or mapped MCP JWT caller) without the Execute scope; this call-tool filter
/// refuses it again on every <c>tools/call</c> — attribute tools, saved-query and host endpoint tools alike — so a host
/// that maps <c>/beacon/mcp</c> itself with a weaker policy (bare <c>RequireAuthorization()</c>, or none) still fails
/// closed. <see cref="McpScopeMessageFilter"/> refuses the caller's other requests. A request over HTTP without an
/// authenticated user is refused too; a request with no user and no HTTP request behind it (a transport without
/// authentication, such as stdio) passes with a warning. Callers without a scope marker (cookie sessions) pass.
/// <para>
/// A refused call never reaches a tool, but it is a tool invocation all the same and is audited (§1.7/§9.5): one audit
/// row with the tool name, the caller's identifiers and a permission error. When the deployment requires the audit and
/// that row could not be written, the refusal answers with the withheld-result message instead.
/// </para>
/// </summary>
internal static class McpScopeCallToolFilter
{
    public const string MissingScopeMessage =
        "Permission denied: this credential does not have the Execute scope that Beacon MCP requires.";

    public const string UnauthenticatedMessage =
        "Permission denied: Beacon MCP requires an authenticated caller.";

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create()
    {
        return next => async (request, cancellationToken) =>
        {
            var user = request.User;
            var refusal = Refusal(user, request.Services);
            var logger = request.Services?
                .GetService<ILoggerFactory>()?
                .CreateLogger(typeof(McpScopeCallToolFilter));
            var tool = request.Params?.Name;

            if (refusal == null)
            {
                if (user == null)
                {
                    logger?.LogWarning(
                        "MCP tool call without a user and without an HTTP request: the scope is not checked. Tool={Tool}",
                        McpAuditService.BoundedToolName(tool));
                }

                return await next(request, cancellationToken);
            }

            // Identifiers only (§1.11): the tool name is bounded, the key id is an id.
            logger?.LogWarning(
                "MCP tool call refused: {Reason} Tool={Tool} ApiKeyId={ApiKeyId}",
                refusal,
                McpAuditService.BoundedToolName(tool),
                user?.FindFirst(McpCallerClaimTypes.ApiKeyId)?.Value);

            await AuditRefusalAsync(request.Services, tool, refusal, cancellationToken);

            var message = McpAuditCallToolFilter.ShouldWithhold(request.Services, resultIsError: true, tool)
                ? McpAuditCallToolFilter.WithheldMessage
                : refusal;

            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = message }]
            };
        };
    }

    /// <summary>
    /// Why <paramref name="user"/> may not use Beacon MCP, or <c>null</c> when it may: a scoped caller needs the Execute
    /// scope, and over HTTP the caller must be authenticated. <c>null</c> for a request without a user and without an
    /// HTTP request behind it.
    /// </summary>
    internal static string? Refusal(ClaimsPrincipal? user, IServiceProvider? services)
    {
        if (user == null)
        {
            return services?.GetService<IHttpContextAccessor>()?.HttpContext != null ? UnauthenticatedMessage : null;
        }

        if (user.Identity?.IsAuthenticated != true)
        {
            return UnauthenticatedMessage;
        }

        return BeaconScopes.SatisfiesExecuteScope(user) ? null : MissingScopeMessage;
    }

    private static async Task AuditRefusalAsync(
        IServiceProvider? services,
        string? tool,
        string refusal,
        CancellationToken cancellationToken)
    {
        var audit = services?.GetService<McpAuditService>();
        if (audit == null)
        {
            return;
        }

        // The caller's identifiers (user, API key, MCP caller) as every tool's audit row records them.
        var userId = services!.GetService<IProjectContext>()?.UserId;
        await audit.LogToolCallAsync(
            sessionId: null,
            userId: userId,
            tool: tool ?? McpAuditService.InvalidToolName,
            parameters: null,
            dataSourceId: null,
            projectId: null,
            executionTimeMs: 0,
            resultRowCount: null,
            errorMessage: refusal,
            ct: cancellationToken);
    }
}

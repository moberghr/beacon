using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Beacon.Core.Mcp;

namespace Beacon.MCP.Services;

/// <summary>
/// Scope enforcement for every MCP request (§1.4), as an incoming message filter so it covers all of them —
/// <c>tools/list</c>, resources, prompts, completions and whatever a host or a later SDK adds. A caller
/// <see cref="McpScopeCallToolFilter.Refusal"/> refuses (a scoped caller without the Execute scope, or an
/// unauthenticated caller over HTTP) gets a JSON-RPC error for every request except three: <c>initialize</c> and
/// <c>ping</c>, which carry no Beacon data, and <c>tools/call</c>, which <see cref="McpScopeCallToolFilter"/> refuses
/// and audits with a tool error. Notifications and responses pass; without a request a refused caller gets nothing.
/// </summary>
internal static class McpScopeMessageFilter
{
    private static readonly HashSet<string> PassThroughMethods = new(StringComparer.Ordinal)
    {
        RequestMethods.Initialize,
        RequestMethods.Ping,
        RequestMethods.ToolsCall
    };

    public static McpMessageFilter Create()
    {
        return next => async (context, cancellationToken) =>
        {
            if (context.JsonRpcMessage is not JsonRpcRequest request || PassThroughMethods.Contains(request.Method))
            {
                await next(context, cancellationToken);
                return;
            }

            var refusal = McpScopeCallToolFilter.Refusal(context.User, context.Services);
            if (refusal == null)
            {
                await next(context, cancellationToken);
                return;
            }

            // Identifiers only (§1.11): the method is caller-chosen, so it is bounded; the key id is an id.
            context.Services?
                .GetService<ILoggerFactory>()?
                .CreateLogger(typeof(McpScopeMessageFilter))
                .LogWarning(
                    "MCP request refused: {Reason} Method={Method} ApiKeyId={ApiKeyId}",
                    refusal,
                    BoundedMethod(request.Method),
                    context.User?.FindFirst(McpCallerClaimTypes.ApiKeyId)?.Value);

            throw new McpProtocolException(refusal, McpErrorCode.InvalidRequest);
        };
    }

    private static string BoundedMethod(string method)
    {
        var isBounded = method.Length is > 0 and <= 64
            && method.All(x => char.IsAsciiLetterOrDigit(x) || x is '/' or '_' or '.' or '-' or ':');

        return isBounded ? method : McpAuditService.InvalidToolName;
    }
}

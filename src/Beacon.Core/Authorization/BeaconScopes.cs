using System.Security.Claims;
using Beacon.Core.Mcp;

namespace Beacon.Core.Authorization;

/// <summary>
/// Scopes of scoped callers (§1.4): API keys and mapped MCP JWT callers, the principals whose <c>auth_method</c> is
/// <c>api_key</c> or <c>mcp_caller</c>. <c>Read</c> allows read-only requests; <c>Execute</c> is needed for anything
/// that changes state or runs SQL, outbound calls or the LLM. Interactive sessions (cookie/OIDC) and REST bearer
/// callers are not scoped; their roles govern them. Every scope gate (the REST scope filter, the Execute-scope policy
/// and the MCP call-tool filter) decides through <see cref="SatisfiesExecuteScope"/>.
/// </summary>
public static class BeaconScopes
{
    public const string Read = nameof(McpCallerScope.Read);
    public const string Execute = nameof(McpCallerScope.Execute);

    /// <summary>
    /// A scope older keys may carry. It never granted more than <see cref="Execute"/>, is read as <see cref="Execute"/>
    /// and can no longer be issued.
    /// </summary>
    public const string LegacyAdmin = "Admin";

    /// <summary>The authorization policy that requires the Execute scope of a scoped caller.</summary>
    public const string ExecuteScopePolicyName = "BeaconApiExecute";

    public static bool IsScopedCaller(ClaimsPrincipal user)
    {
        return user.HasClaim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.ApiKeyAuthMethod)
            || user.HasClaim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.McpCallerAuthMethod);
    }

    /// <summary>A caller that is not scoped passes; a scoped caller must hold the Execute scope.</summary>
    public static bool SatisfiesExecuteScope(ClaimsPrincipal user)
    {
        return !IsScopedCaller(user) || user.HasClaim(McpCallerClaimTypes.Scope, Execute);
    }
}

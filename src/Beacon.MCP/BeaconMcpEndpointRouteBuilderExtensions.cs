using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Beacon.Core.Authorization;
using Beacon.Core.Mcp;

namespace Beacon.MCP;

public static class BeaconMcpEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the Beacon MCP server at <c>/beacon/mcp</c> behind the Execute-scope policy (§1.4,
    /// <see cref="BeaconScopes.ExecuteScopePolicyName"/>, registered by <c>AddBeaconApiAuthorization()</c>) and
    /// <see cref="McpCredentialPolicy"/>: an API key or mapped MCP JWT caller without the Execute scope gets 403, so does a
    /// signed-in browser session (Beacon's or the host's), and an unauthenticated caller the challenge. Use it instead of
    /// <c>MapMcp("/beacon/mcp")</c>. Every request is checked again inside the MCP layer (tool calls answer a tool error,
    /// other requests a JSON-RPC error), so a host that maps the route itself with a weaker policy still fails closed —
    /// but it loses the 403 at the door.
    /// </summary>
    public static IEndpointConventionBuilder MapBeaconMcp(this IEndpointRouteBuilder endpoints)
    {
        return endpoints
            .MapMcp(McpDiscoveryPaths.McpPath)
            .RequireAuthorization(BeaconScopes.ExecuteScopePolicyName)
            .RequireAuthorization(McpCredentialPolicy);
    }

    /// <summary>
    /// The caller presents an MCP credential: an API key or a bearer token Beacon mapped (<c>auth_method</c>
    /// <c>api_key</c> or <c>mcp_caller</c>). A cookie or SSO session is not one, whoever signed it in. A policy object
    /// rather than a registered name, so mapping the route needs no further registration.
    /// </summary>
    public static AuthorizationPolicy McpCredentialPolicy { get; } = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(x => BeaconScopes.IsScopedCaller(x.User))
        .Build();
}

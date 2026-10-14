using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Beacon.Core.Authorization;
using Beacon.Core.Mcp;

namespace Beacon.MCP;

public static class BeaconMcpEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the Beacon MCP server at <c>/beacon/mcp</c> behind the Execute-scope policy (§1.4,
    /// <see cref="BeaconScopes.ExecuteScopePolicyName"/>, registered by <c>AddBeaconApiAuthorization()</c>): an API key
    /// or mapped MCP JWT caller without the Execute scope gets 403, an unauthenticated caller the challenge. Use it
    /// instead of <c>MapMcp("/beacon/mcp")</c>. Every request is scope-checked again inside the MCP layer (tool calls
    /// answer a tool error, other requests a JSON-RPC error), so a host that maps the route itself with a weaker policy
    /// still fails closed — but it loses the 403 at the door.
    /// </summary>
    public static IEndpointConventionBuilder MapBeaconMcp(this IEndpointRouteBuilder endpoints)
    {
        return endpoints
            .MapMcp(McpDiscoveryPaths.McpPath)
            .RequireAuthorization(BeaconScopes.ExecuteScopePolicyName);
    }
}

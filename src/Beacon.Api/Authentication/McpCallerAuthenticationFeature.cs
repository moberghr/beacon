using System.Security.Claims;

namespace Beacon.Api.Authentication;

/// <summary>
/// The principal Beacon's bearer middleware authenticated on <c>/beacon/mcp</c>, kept on the request so it survives a host
/// authentication that replaces <c>HttpContext.User</c> after it (a host's cookie scheme runs later in the pipeline). The
/// MCP gateway's <c>BeaconMcp</c> authentication scheme answers with it, so the MCP route is decided by the MCP credential
/// alone, never by a browser session.
/// </summary>
public sealed class McpCallerAuthenticationFeature(ClaimsPrincipal principal)
{
    public ClaimsPrincipal Principal { get; } = principal;
}

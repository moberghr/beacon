using System.Security.Claims;

namespace Beacon.Core.Mcp;

/// <summary>
/// Maps a validated JWT principal calling <c>/beacon/mcp</c> to a Beacon caller: its projects, scope, audit
/// identity and host permission set. The default is <see cref="ConfiguredMcpCallerMapper"/> (driven by
/// <c>Beacon:Mcp:Callers</c>); a host replaces it by registering its own implementation before
/// <c>AddBeaconServices</c> or by replacing the registration.
/// </summary>
public interface IMcpCallerMapper
{
    /// <summary>
    /// Returns the caller, or <c>null</c> when the principal is not an accepted caller. A null result gets no
    /// project access and no scope, so the MCP route rejects it with 403.
    /// </summary>
    /// <param name="jwtPrincipal">The raw token claims (unmapped claim names such as <c>oid</c>, <c>tid</c>, <c>groups</c>).</param>
    Task<McpCaller?> MapAsync(ClaimsPrincipal jwtPrincipal, CancellationToken cancellationToken);
}

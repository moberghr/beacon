using System.Security.Claims;

namespace Beacon.Core.Mcp;

/// <summary>
/// Who a validated MCP bearer token is in the host application: the seam that lets the host's own users decide who may use
/// MCP and what a tool call runs as, in place of <c>Beacon:Mcp:Callers</c>. <see cref="HostUserMcpCallerMapper"/> asks it
/// whether a token may use MCP at all, and <see cref="HostUserMcpHostPrincipalFactory"/> asks it again on every tool
/// call, so an account the host deactivates loses MCP on its next call.
/// </summary>
public interface IMcpHostUserResolver
{
    /// <summary>
    /// The host user the token's person is, or <c>null</c> to refuse the token. Look the user up in the host's own store
    /// by <see cref="McpTokenIdentity.ObjectId"/>; never create or link one here, and refuse when the id names more than
    /// one active user (the token then does not say which of them it is).
    /// </summary>
    Task<McpHostUser?> FindAsync(McpTokenIdentity token, CancellationToken cancellationToken);
}

/// <summary>What Beacon validated about a delegated Entra access token: never a claim the token could set for itself.</summary>
/// <param name="TenantId">The token's tenant (<c>tid</c>), one Beacon accepts.</param>
/// <param name="ObjectId">The person's directory object id (<c>oid</c>), stable across applications, unlike <c>sub</c>.</param>
/// <param name="ClientId">The client application the token was issued to (<c>azp</c>, or <c>appid</c> in a v1 token).</param>
/// <param name="Scopes">The delegated scopes (<c>scp</c>).</param>
public sealed record McpTokenIdentity(string TenantId, string ObjectId, string? ClientId, IReadOnlyList<string> Scopes);

/// <summary>A host user an MCP token resolved to.</summary>
/// <param name="Id">The host's id for the user, for the host's own logs.</param>
/// <param name="DisplayName">The name Beacon shows for the caller; never logged.</param>
/// <param name="Claims">
/// The claims a tool call's principal carries in the host — the ones the host's own sign-in adds that its endpoints read
/// (its user id, say). The principal's identity has the authentication type
/// <see cref="DefaultMcpHostPrincipalFactory.UserAuthenticationType"/>, which <see cref="McpHostPrincipalExtensions.IsBeaconMcpCall"/>
/// recognises, so the host can tell a tool call from a browser session.
/// </param>
public sealed record McpHostUser(string Id, string DisplayName, IReadOnlyList<Claim> Claims);

/// <summary>Recognises the principal a host endpoint tool call runs as.</summary>
public static class McpHostPrincipalExtensions
{
    /// <summary>
    /// True when <paramref name="principal"/> is the one Beacon built for an MCP tool call
    /// (<see cref="DefaultMcpHostPrincipalFactory"/> or <see cref="HostUserMcpHostPrincipalFactory"/>): an agent acting for
    /// a person, which a host may hold to less than the person's browser session (their own records only, say).
    /// </summary>
    public static bool IsBeaconMcpCall(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return principal.Identities.Any(x => x.IsAuthenticated
            && x.AuthenticationType is DefaultMcpHostPrincipalFactory.UserAuthenticationType or DefaultMcpHostPrincipalFactory.SystemAuthenticationType);
    }
}

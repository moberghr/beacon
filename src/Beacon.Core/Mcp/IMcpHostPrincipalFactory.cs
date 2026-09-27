using System.Security.Claims;

namespace Beacon.Core.Mcp;

/// <summary>
/// Builds the principal a host endpoint tool runs as inside the host application. The endpoint's own authorization
/// policies are evaluated against this principal, never against the MCP token. The default
/// (<see cref="DefaultMcpHostPrincipalFactory"/>) gives a system caller its configured <c>HostClaims</c> and a user
/// caller its identity claims only; a host that maps its users to its own permissions registers its own factory.
/// </summary>
public interface IMcpHostPrincipalFactory
{
    /// <summary>
    /// Returns the host principal, or <c>null</c> to refuse the call (the tool then fails closed with a clear error).
    /// </summary>
    /// <param name="caller">The mapped MCP caller (from <see cref="IMcpCallerMapper"/>).</param>
    /// <param name="mcpPrincipal">The principal of the MCP request, as the JWT middleware minted it.</param>
    Task<ClaimsPrincipal?> CreateAsync(McpCaller caller, ClaimsPrincipal mcpPrincipal, CancellationToken cancellationToken);
}

/// <summary>
/// The default host principal: a system caller gets <see cref="McpCaller.HostClaims"/> plus its name; a user caller
/// gets its name, email, preferred username and object id copied from the MCP principal plus whatever
/// <see cref="McpCaller.HostClaims"/> the caller mapper assigned (none with the configured mapper). Entra roles and
/// groups are deliberately NOT copied: a host role that happens to share a name would be granted implicitly. Hosts
/// map users to their own permissions in their own <see cref="IMcpHostPrincipalFactory"/>.
/// </summary>
public sealed class DefaultMcpHostPrincipalFactory : IMcpHostPrincipalFactory
{
    public const string SystemAuthenticationType = "BeaconMcpSystem";
    public const string UserAuthenticationType = "BeaconMcpUser";

    private const string ObjectIdentifierClaimType = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    private static readonly IReadOnlySet<string> CopiedUserClaimTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        ClaimTypes.Name,
        ClaimTypes.Email,
        "name",
        "email",
        "preferred_username",
        "oid",
        ObjectIdentifierClaimType
    };

    public Task<ClaimsPrincipal?> CreateAsync(McpCaller caller, ClaimsPrincipal mcpPrincipal, CancellationToken cancellationToken)
    {
        var claims = caller.Kind == McpCallerKind.System
            ? []
            : mcpPrincipal.Claims
                .Where(x => CopiedUserClaimTypes.Contains(x.Type))
                .Select(x => new Claim(x.Type, x.Value))
                .ToList();

        if (!claims.Any(x => x.Type == ClaimTypes.Name))
        {
            claims.Add(new Claim(ClaimTypes.Name, caller.Name));
        }

        claims.AddRange(caller.HostClaims.Select(x => new Claim(x.Type, x.Value)));

        var authenticationType = caller.Kind == McpCallerKind.System ? SystemAuthenticationType : UserAuthenticationType;
        var identity = new ClaimsIdentity(claims, authenticationType, ClaimTypes.Name, ClaimTypes.Role);

        return Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(identity));
    }
}

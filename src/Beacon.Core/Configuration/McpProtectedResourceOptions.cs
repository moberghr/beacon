namespace Beacon.Core.Configuration;

/// <summary>
/// What the RFC 9728 protected-resource metadata (<c>/.well-known/oauth-protected-resource</c>) advertises to OAuth
/// clients of <c>/beacon/mcp</c>, bound from <c>Beacon:Mcp:ProtectedResource</c>. For a host whose MCP callers sign in
/// with an external identity provider (Microsoft Entra ID, validated through <c>AddBeaconJwtAuthentication</c>) rather
/// than Beacon's own OIDC login: an MCP client reads <c>authorization_servers[0]</c>, fetches that server's metadata
/// and requests the scopes in <c>scopes_supported</c>, so both have to name the external IdP.
/// <para>
/// An absent section keeps the original document: Beacon's OIDC authority (when <c>Beacon:Authentication:Oidc</c> is
/// enabled, otherwise no <c>authorization_servers</c>) and the Beacon scope name <c>Execute</c> (the Admin scope is retired).
/// </para>
/// </summary>
public sealed class McpProtectedResourceOptions
{
    public const string SectionName = "Beacon:Mcp:ProtectedResource";

    /// <summary>
    /// Authorization server issuer identifiers to publish, verbatim and in order (clients use the first), e.g.
    /// <c>https://login.microsoftonline.com/{tenant-id}/v2.0</c>. Each must equal the <c>issuer</c> of the server's
    /// own metadata document. Takes precedence over <see cref="DeriveAuthorizationServerFromJwtIssuer"/> and over the
    /// Beacon OIDC authority.
    /// </summary>
    public List<string> AuthorizationServers { get; set; } = [];

    /// <summary>
    /// When <see cref="AuthorizationServers"/> is empty, publish the bearer JWT issuer
    /// (<c>JwtAuthenticationOptions.Validation.ValidIssuer</c> / <c>ValidIssuers</c>) as the authorization server.
    /// With several issuers the Entra v2 one (ending in <c>/v2.0</c>) is chosen, because only the v2 metadata
    /// advertises the v2 endpoints that issue scope-based tokens. Default off, so existing hosts keep their document.
    /// </summary>
    public bool DeriveAuthorizationServerFromJwtIssuer { get; set; }

    /// <summary>
    /// Scopes to publish instead of Beacon's internal <c>Execute</c> name, as the external IdP knows
    /// them, e.g. <c>api://{api-app-client-id}/mcp.access</c>. Clients request these verbatim. Also used for the server
    /// card's <c>authentication.scopes_supported</c>. Empty keeps the Beacon names.
    /// </summary>
    public List<string> ScopesSupported { get; set; } = [];
}

using Beacon.Core.Authorization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Beacon.Core.Authentication;
using Beacon.Core.Configuration;
using Beacon.Core.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Authentication;

namespace Beacon.MCP.Discovery;

/// <summary>
/// Builds the anonymous discovery documents for the Beacon MCP server:
/// the RFC 9728 protected-resource metadata and the server card.
/// Pure functions of (public base URL, authorization servers, scopes) so they are unit-testable without a host.
/// </summary>
internal static class McpDiscoveryDocuments
{
    /// <summary>
    /// Beacon's server name, unless <see cref="McpProtectedResourceOptions.ResourceName"/> names the host's
    /// (<see cref="ServerNameFor"/>): the server card's <c>name</c> and the MCP ServerInfo (<c>ServiceConfiguration</c>).
    /// </summary>
    public const string ServerName = "Beacon";

    /// <summary>
    /// The Beacon.MCP assembly's informational version (the release tag a package is built from), without the
    /// <c>+commit</c> suffix the SDK appends.
    /// </summary>
    public static readonly string ServerVersion = VersionFrom(
        typeof(McpDiscoveryDocuments).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        typeof(McpDiscoveryDocuments).Assembly.GetName().Version);

    /// <summary>Reported when the assembly carries no usable version.</summary>
    public const string FallbackVersion = "0.0.0";

    // Aliases only — the paths themselves live in Beacon.Core so the auth middlewares in
    // Beacon.Api can allow-list the exact same values without Api referencing MCP (§2.4).
    public const string McpPath = McpDiscoveryPaths.McpPath;
    public const string ProtectedResourceMetadataPath = McpDiscoveryPaths.ProtectedResourceMetadataPath;
    public const string ServerCardPath = McpDiscoveryPaths.ServerCardPath;
    public const string DocumentationUrl = "https://moberghr.github.io/beacon/features/mcp-server/";

    /// <summary>The <c>resource_name</c> published unless <see cref="McpProtectedResourceOptions.ResourceName"/> sets one.</summary>
    public const string DefaultResourceName = "Beacon MCP";

    public static readonly IReadOnlyList<string> ScopesSupported = [BeaconScopes.Execute];

    /// <summary>The OpenID scope that makes an identity provider issue a refresh token with the access token.</summary>
    public const string OfflineAccessScope = McpProtectedResourceOptions.OfflineAccessScope;

    private const string Description =
        "Governed, read-only access to a project's data sources: natural-language ask, "
        + "schema-grounded SQL generation, catalog search, documentation, and SQL validation tools.";

    /// <summary>
    /// RFC 9728 protected-resource metadata with Beacon's legacy values: <paramref name="oidcAuthority"/> (the
    /// configured OIDC authority when SSO is enabled, else <see langword="null"/>) and the Beacon scope names.
    /// </summary>
    public static JsonObject BuildProtectedResourceMetadata(string publicBaseUrl, string? oidcAuthority)
    {
        return BuildProtectedResourceMetadata(
            publicBaseUrl,
            string.IsNullOrWhiteSpace(oidcAuthority) ? [] : [oidcAuthority],
            ScopesSupported);
    }

    /// <summary>
    /// RFC 9728 protected-resource metadata. <c>resource</c> is the canonical MCP endpoint URL — the same value as the
    /// server card's <c>endpoint</c>. API-key-only deployments still get metadata (bearer header + scopes), but with no
    /// <paramref name="authorizationServers"/> the <c>authorization_servers</c> member is omitted entirely, because the
    /// SDK type serializes an unset list as <c>[]</c>, which RFC 9728 clients would read as "no authorization server
    /// exists" rather than "not applicable".
    /// </summary>
    /// <param name="resourceName"><see cref="McpProtectedResourceOptions.ResourceName"/>; empty publishes <see cref="DefaultResourceName"/>.</param>
    /// <param name="resourceDocumentation"><see cref="McpProtectedResourceOptions.ResourceDocumentation"/>; empty publishes <see cref="DocumentationUrl"/>.</param>
    public static JsonObject BuildProtectedResourceMetadata(
        string publicBaseUrl,
        IReadOnlyList<string> authorizationServers,
        IReadOnlyList<string> scopesSupported,
        string? resourceName = null,
        string? resourceDocumentation = null)
    {
        var metadata = new ProtectedResourceMetadata
        {
            Resource = $"{publicBaseUrl}{McpPath}",
            BearerMethodsSupported = ["header"],
            ScopesSupported = [.. scopesSupported],
            ResourceName = string.IsNullOrWhiteSpace(resourceName) ? DefaultResourceName : resourceName.Trim(),
            ResourceDocumentation = DocumentationOrDefault(resourceDocumentation)
        };

        if (authorizationServers.Count > 0)
        {
            metadata.AuthorizationServers = [.. authorizationServers];
        }

        var document = (JsonObject)JsonSerializer.SerializeToNode(metadata, McpJsonUtilities.DefaultOptions)!;
        if (authorizationServers.Count == 0)
        {
            document.Remove("authorization_servers");
        }

        return document;
    }

    /// <summary>
    /// The <c>authorization_servers</c> to publish, first match wins: the explicit
    /// <see cref="McpProtectedResourceOptions.AuthorizationServers"/>; the bearer JWT issuer when
    /// <see cref="McpProtectedResourceOptions.DeriveAuthorizationServerFromJwtIssuer"/> is on; Beacon's own OIDC
    /// authority (legacy); none.
    /// </summary>
    public static IReadOnlyList<string> ResolveAuthorizationServers(
        McpProtectedResourceOptions options,
        JwtAuthenticationOptions? jwtOptions,
        string? oidcAuthority)
    {
        var configured = Clean(options.AuthorizationServers);
        if (configured.Count > 0)
        {
            return configured;
        }

        if (options.DeriveAuthorizationServerFromJwtIssuer)
        {
            var issuer = DeriveFromJwtIssuer(jwtOptions);
            return issuer == null ? [] : [issuer];
        }

        return string.IsNullOrWhiteSpace(oidcAuthority) ? [] : [oidcAuthority];
    }

    /// <summary>The configured external-IdP scopes, or Beacon's own scope names when none are configured.</summary>
    public static IReadOnlyList<string> ResolveScopesSupported(McpProtectedResourceOptions options)
    {
        var configured = Clean(options.ScopesSupported);
        return configured.Count > 0 ? configured : ScopesSupported;
    }

    // The issuer is published verbatim (no trailing-slash trimming): RFC 8414 §3.3 / OIDC Discovery §4.3 make clients
    // compare it with the issuer in the server's metadata. Templated issuers ("{tenantid}") and non-URLs are skipped.
    // Entra v2 first: its metadata is the one whose endpoints issue scope-based (v2) tokens.
    private static string? DeriveFromJwtIssuer(JwtAuthenticationOptions? jwtOptions)
    {
        if (jwtOptions == null || !jwtOptions.EnableBearerAuthentication)
        {
            return null;
        }

        var candidates = jwtOptions.Validation.EffectiveIssuers()
            .Where(x => !x.Contains('{')
                && Uri.TryCreate(x, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            .ToList();

        return candidates.FirstOrDefault(x => x.TrimEnd('/').EndsWith("/v2.0", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault();
    }

    private static List<string> Clean(IEnumerable<string>? values)
    {
        return (values ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Server card per SEP-2127. NOTE: SEP-2127 is a DRAFT proposal — this shape (name, version,
    /// description, transport, endpoint, authentication summary, tools, documentation) is
    /// provisional and may need adjusting when the SEP is accepted.
    /// </summary>
    public static JsonObject BuildServerCard(string publicBaseUrl)
    {
        return BuildServerCard(publicBaseUrl, ScopesSupported);
    }

    /// <summary>
    /// Server card whose <c>endpoint</c>, <c>scopes_supported</c> and <c>resource_metadata</c> match the
    /// protected-resource metadata and the <c>WWW-Authenticate</c> challenge.
    /// </summary>
    /// <param name="builtInTools">The built-in tools the host serves (<see cref="BeaconMcpOptions.BuiltInTools"/>); null lists all.</param>
    /// <param name="documentation"><see cref="McpProtectedResourceOptions.ResourceDocumentation"/>; empty publishes <see cref="DocumentationUrl"/>.</param>
    /// <param name="name"><see cref="McpProtectedResourceOptions.ResourceName"/>; empty publishes <see cref="ServerName"/>.</param>
    /// <param name="description"><see cref="BeaconMcpOptions.ServerDescription"/>; empty publishes Beacon's description.</param>
    public static JsonObject BuildServerCard(
        string publicBaseUrl,
        IReadOnlyList<string> scopesSupported,
        IReadOnlyCollection<string>? builtInTools = null,
        string? documentation = null,
        string? name = null,
        string? description = null)
    {
        var tools = new JsonArray();
        foreach (var tool in McpToolCatalog.Tools.Where(x => builtInTools?.Contains(x.Name) != false))
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["title"] = tool.Title
            });
        }

        return new JsonObject
        {
            ["name"] = ServerNameFor(name),
            ["version"] = ServerVersion,
            ["description"] = string.IsNullOrWhiteSpace(description) ? Description : description.Trim(),
            ["transport"] = "streamable-http",
            ["endpoint"] = $"{publicBaseUrl}{McpPath}",
            ["authentication"] = new JsonObject
            {
                ["type"] = "bearer",
                ["scopes_supported"] = new JsonArray([.. scopesSupported.Select(x => JsonValue.Create(x))]),
                ["resource_metadata"] = $"{publicBaseUrl}{ProtectedResourceMetadataPath}"
            },
            ["tools"] = tools,
            ["documentation"] = DocumentationOrDefault(documentation)
        };
    }

    /// <summary>
    /// The informational version without its build metadata (<c>4.5.1+3f20291</c> is <c>4.5.1</c>); without one, the
    /// assembly version (major.minor.build); without either, <see cref="FallbackVersion"/>.
    /// </summary>
    internal static string VersionFrom(string? informationalVersion, Version? assemblyVersion)
    {
        var version = informationalVersion?.Split('+', 2)[0].Trim();
        if (!string.IsNullOrEmpty(version))
        {
            return version;
        }

        return assemblyVersion?.ToString(3) ?? FallbackVersion;
    }

    /// <summary>The server's name: <see cref="McpProtectedResourceOptions.ResourceName"/> when set, else <see cref="ServerName"/>.</summary>
    internal static string ServerNameFor(string? resourceName) =>
        string.IsNullOrWhiteSpace(resourceName) ? ServerName : resourceName.Trim();

    private static string DocumentationOrDefault(string? documentation) =>
        string.IsNullOrWhiteSpace(documentation) ? DocumentationUrl : documentation.Trim();
}

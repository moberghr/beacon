using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Authentication;

namespace Beacon.MCP.Discovery;

/// <summary>
/// Builds the anonymous discovery documents for the Beacon MCP server:
/// the RFC 9728 protected-resource metadata and the server card.
/// Pure functions of (public base URL, OIDC authority) so they are unit-testable without a host.
/// </summary>
internal static class McpDiscoveryDocuments
{
    /// <summary>Single source for the server identity — also used by <c>ServiceConfiguration</c> for the MCP ServerInfo.</summary>
    public const string ServerName = "Beacon";
    public const string ServerVersion = "2.0.5";

    public const string McpPath = "/beacon/mcp";
    public const string ProtectedResourceMetadataPath = "/.well-known/oauth-protected-resource";
    public const string ServerCardPath = "/.well-known/mcp/server-card.json";
    public const string DocumentationUrl = "https://moberghr.github.io/beacon/features/mcp-server/";

    public static readonly IReadOnlyList<string> ScopesSupported = ["Execute", "Admin"];

    private const string Description =
        "Governed, read-only access to a project's data sources: natural-language ask, "
        + "schema-grounded SQL generation, catalog search, documentation, and SQL validation tools.";

    /// <summary>
    /// RFC 9728 protected-resource metadata. <paramref name="oidcAuthority"/> is the configured
    /// OIDC authority when SSO is enabled, else <see langword="null"/> — API-key-only deployments
    /// still get metadata (bearer header + scopes), but <c>authorization_servers</c> is omitted
    /// entirely because the SDK type serializes an unset list as <c>[]</c>, which RFC 9728 clients
    /// would read as "no authorization server exists" rather than "not applicable".
    /// </summary>
    public static JsonObject BuildProtectedResourceMetadata(string publicBaseUrl, string? oidcAuthority)
    {
        var metadata = new ProtectedResourceMetadata
        {
            Resource = $"{publicBaseUrl}{McpPath}",
            BearerMethodsSupported = ["header"],
            ScopesSupported = [.. ScopesSupported],
            ResourceName = "Beacon MCP",
            ResourceDocumentation = DocumentationUrl
        };

        if (!string.IsNullOrWhiteSpace(oidcAuthority))
        {
            metadata.AuthorizationServers = [oidcAuthority];
        }

        var document = (JsonObject)JsonSerializer.SerializeToNode(metadata, McpJsonUtilities.DefaultOptions)!;
        if (string.IsNullOrWhiteSpace(oidcAuthority))
        {
            document.Remove("authorization_servers");
        }

        return document;
    }

    /// <summary>
    /// Server card per SEP-2127. NOTE: SEP-2127 is a DRAFT proposal — this shape (name, version,
    /// description, transport, endpoint, authentication summary, tools, documentation) is
    /// provisional and may need adjusting when the SEP is accepted.
    /// </summary>
    public static JsonObject BuildServerCard(string publicBaseUrl)
    {
        var tools = new JsonArray();
        foreach (var tool in McpToolCatalog.Tools)
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["title"] = tool.Title
            });
        }

        return new JsonObject
        {
            ["name"] = ServerName,
            ["version"] = ServerVersion,
            ["description"] = Description,
            ["transport"] = "streamable-http",
            ["endpoint"] = $"{publicBaseUrl}{McpPath}",
            ["authentication"] = new JsonObject
            {
                ["type"] = "bearer",
                ["scopes_supported"] = new JsonArray([.. ScopesSupported.Select(x => JsonValue.Create(x))]),
                ["resource_metadata"] = $"{publicBaseUrl}{ProtectedResourceMetadataPath}"
            },
            ["tools"] = tools,
            ["documentation"] = DocumentationUrl
        };
    }
}

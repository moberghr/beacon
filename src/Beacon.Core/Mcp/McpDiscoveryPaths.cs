namespace Beacon.Core.Mcp;

/// <summary>
/// The MCP transport and discovery paths, and the exact set that is served anonymously.
/// This lives in Core because two sibling projects need the same values and must not reference
/// each other: Beacon.MCP maps the routes, and the auth middlewares in Beacon.Api allow-list them.
/// This type is the single source of truth — never re-declare these paths anywhere else.
/// </summary>
public static class McpDiscoveryPaths
{
    public const string McpPath = "/beacon/mcp";
    public const string ProtectedResourceMetadataPath = "/.well-known/oauth-protected-resource";
    public const string ServerCardPath = "/.well-known/mcp/server-card.json";

    /// <summary>
    /// The exact anonymous discovery paths. Allow-lists must match ONLY these exact paths, never a
    /// <c>/.well-known</c> prefix: a prefix match would let <c>/.well-known/anything</c> reach the
    /// SPA fallback anonymously (R6-2).
    /// </summary>
    public static readonly IReadOnlyList<string> AnonymousDiscoveryPaths =
    [
        ProtectedResourceMetadataPath,
        $"{ProtectedResourceMetadataPath}{McpPath}",
        ServerCardPath
    ];
}

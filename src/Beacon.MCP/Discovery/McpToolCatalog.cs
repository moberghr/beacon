namespace Beacon.MCP.Discovery;

/// <summary>
/// The single static source for the MCP tool list used by discovery documents (server card) and
/// the playground facade. Order matches the playground UI. A reflection test
/// (<c>McpDiscoveryDocumentTests.ToolCatalog_MatchesLiveToolAttributes</c>) pins every entry
/// against the live <c>[McpServerTool]</c> attributes in this assembly, so adding a tool without
/// updating this table fails the build.
/// </summary>
internal static class McpToolCatalog
{
    public static readonly IReadOnlyList<McpToolCatalogEntry> Tools =
    [
        new McpToolCatalogEntry("get_context", "Project Overview"),
        new McpToolCatalogEntry("ask", "Ask a Data Question"),
        new McpToolCatalogEntry("query", "Run Read-Only Query"),
        new McpToolCatalogEntry("get_documentation", "Get Documentation"),
        new McpToolCatalogEntry("search", "Search Catalog"),
        new McpToolCatalogEntry("feedback", "Record Answer Feedback"),
        new McpToolCatalogEntry("dry_run", "Validate SQL Without Executing"),
        new McpToolCatalogEntry("get_query_context", "Get Grounding Context for a Question")
    ];

    public static IReadOnlyList<string> Names { get; } = Tools
        .Select(x => x.Name)
        .ToList();
}

internal sealed record McpToolCatalogEntry(string Name, string Title);

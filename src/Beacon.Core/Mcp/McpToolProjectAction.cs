using Beacon.Core.Authorization;

namespace Beacon.Core.Mcp;

/// <summary>
/// The project action a built-in MCP tool needs in the project it runs in: the catalog tools read it
/// (<see cref="ProjectAction.Read"/>); every other tool — the SQL-carrying tools, feedback, and any name not listed here
/// — works ad hoc in the project (<see cref="ProjectAction.Query"/>), so a tool added without an entry is held to the
/// stricter action. The one map the playground route's project check and the playground service both decide by.
/// </summary>
public static class McpToolProjectAction
{
    private static readonly HashSet<string> ReadTools = new(StringComparer.Ordinal)
    {
        "get_context",
        "search",
        "get_documentation",
        "get_query_context"
    };

    public static ProjectAction Of(string toolName)
    {
        return ReadTools.Contains(toolName) ? ProjectAction.Read : ProjectAction.Query;
    }
}

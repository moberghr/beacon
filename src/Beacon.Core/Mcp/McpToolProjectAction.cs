using Beacon.Core.Authorization;

namespace Beacon.Core.Mcp;

/// <summary>
/// The project action a built-in MCP tool needs in the project it runs in: the SQL-carrying tools and feedback work ad
/// hoc in the project (<see cref="ProjectAction.Query"/>); the catalog tools read it (<see cref="ProjectAction.Read"/>).
/// The one map the playground route's project check and the playground service both decide by.
/// </summary>
public static class McpToolProjectAction
{
    public static ProjectAction Of(string toolName)
    {
        return toolName is "query" or "ask" or "dry_run" or "feedback" ? ProjectAction.Query : ProjectAction.Read;
    }
}

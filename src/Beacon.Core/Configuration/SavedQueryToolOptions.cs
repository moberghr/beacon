namespace Beacon.Core;

/// <summary>How approved saved queries are exposed as MCP tools (<c>q_&lt;name&gt;</c>).</summary>
public class SavedQueryToolOptions
{
    /// <summary>
    /// Up to this many saved-query tools visible to one caller, each is its own MCP tool <c>q_&lt;name&gt;</c>. Above
    /// it, the caller reaches them through <c>search_saved_queries</c> and <c>run_saved_query</c> so a large
    /// catalog does not flood the agent's tool list. Default 25.
    /// </summary>
    public int NamedToolLimit { get; set; } = 25;
}

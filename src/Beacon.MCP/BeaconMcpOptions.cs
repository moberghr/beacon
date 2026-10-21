using Beacon.MCP.Discovery;

namespace Beacon.MCP;

/// <summary>Options for <see cref="ServiceConfiguration.AddBeaconMcp(Microsoft.Extensions.DependencyInjection.IServiceCollection, Action{BeaconMcpOptions})"/>.</summary>
public sealed class BeaconMcpOptions
{
    /// <summary>The names of Beacon's eight built-in tools.</summary>
    public static IReadOnlyList<string> AllBuiltInTools => McpToolCatalog.Names;

    /// <summary>
    /// The built-in tools served, by name (<see cref="AllBuiltInTools"/>). A tool left out is not listed and does not
    /// exist for <c>tools/call</c>; the server card lists only the ones served. A host with no data sources (Beacon as
    /// the MCP gateway for its own endpoints) keeps the document tools (<c>get_context</c>, <c>search</c>,
    /// <c>get_documentation</c>) and drops the SQL ones. A name Beacon does not have fails startup. Default: all eight.
    /// </summary>
    public ISet<string> BuiltInTools { get; } = new HashSet<string>(McpToolCatalog.Names, StringComparer.Ordinal);

    /// <summary>
    /// Serve approved saved queries as <c>q_&lt;name&gt;</c> tools (or <c>search_saved_queries</c> /
    /// <c>run_saved_query</c>). Default true.
    /// </summary>
    public bool SavedQueryTools { get; set; } = true;

    /// <summary>
    /// The instructions sent to clients at <c>initialize</c>. Null (the default) keeps Beacon's, which describe the
    /// data-source workflow; a host that serves only its own endpoints and documents describes those instead.
    /// </summary>
    public string? ServerInstructions { get; set; }

    internal void Validate()
    {
        var unknown = BuiltInTools
            .Where(x => !McpToolCatalog.Names.Contains(x, StringComparer.Ordinal))
            .ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"AddBeaconMcp: unknown built-in tool(s) {string.Join(", ", unknown)}. Beacon has: {string.Join(", ", McpToolCatalog.Names)}.");
        }
    }
}

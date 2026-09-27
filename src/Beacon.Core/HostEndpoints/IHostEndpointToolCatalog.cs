namespace Beacon.Core.HostEndpoints;

/// <summary>One host endpoint tool as the project brief lists it.</summary>
/// <param name="Name">The declared name (what <c>call_api</c> takes).</param>
/// <param name="ToolName">The MCP tool name (<c>api_&lt;name&gt;</c>).</param>
/// <param name="Description">The first line of the tool description.</param>
public sealed record HostEndpointToolSummary(string Name, string ToolName, string Description);

/// <summary>The host endpoint tools of one project.</summary>
/// <param name="CatalogMode">True when the tools are reached through <c>search_api</c> / <c>call_api</c> rather than by name.</param>
public sealed record HostEndpointToolListing(IReadOnlyList<HostEndpointToolSummary> Tools, bool CatalogMode);

/// <summary>
/// Read-only view of the host endpoint tools for Core consumers (the project brief). Implemented by Beacon.MCP when
/// the host calls <c>AddHostEndpointTools</c>; absent otherwise.
/// </summary>
public interface IHostEndpointToolCatalog
{
    /// <summary>The tools that belong to <paramref name="projectId"/>, or null when that project has none.</summary>
    Task<HostEndpointToolListing?> GetForProjectAsync(int projectId, CancellationToken cancellationToken);
}

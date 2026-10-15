using System.Security.Claims;
using System.Text.Json;
using Beacon.Core.Mcp;

namespace Beacon.Core.Authorization;

/// <summary>
/// The project restriction a principal carries in its <c>allowed_projects</c> claim (§1.4), the claim API keys and
/// mapped MCP JWT callers are issued with. Whoever carries the claim is restricted by it.
/// </summary>
internal static class ProjectRestriction
{
    /// <summary>
    /// Null when the principal carries no <c>allowed_projects</c> claim; otherwise the only project ids it may touch.
    /// A blank or malformed claim is an empty list, which denies every project.
    /// </summary>
    public static IReadOnlyList<int>? Of(ClaimsPrincipal? user)
    {
        var restriction = user?.FindFirst(McpCallerClaimTypes.AllowedProjects);
        if (restriction == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(restriction.Value))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<int>>(restriction.Value) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

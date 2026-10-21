using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;

namespace Beacon.Gateway;

/// <summary>
/// The part of Beacon's REST API the gateway's admin UI serves: who the user is, the antiforgery token, health, the host
/// project and its documents (read only), and the MCP gateway itself — the audit, the learning views, the tool catalogue
/// and the settings (the host project's editable). Every other endpoint <c>MapBeaconApi</c> maps (data sources, queries,
/// migrations, subscriptions, API keys, users, the playground, ...) answers 404 with no Beacon code run. A Beacon upgrade
/// that adds an endpoint gets a 404 until it is listed here, and a test enumerates every mapped endpoint against the list.
/// </summary>
internal static class BeaconGatewayApiProfile
{
    /// <summary>The endpoints served, as <c>"METHOD /route/pattern"</c> exactly as Beacon declares them.</summary>
    public static IReadOnlySet<string> Allowed { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "GET /beacon/api/auth/me",
        "GET /beacon/api/auth/sso",
        "GET /beacon/api/auth/permissions",
        "GET /beacon/api/csrf",
        "GET /beacon/api/health",

        "GET /beacon/api/projects/",
        "GET /beacon/api/projects/{id:int}",
        "GET /beacon/api/projects/{id:int}/documentation",
        "GET /beacon/api/projects/{id:int}/imported-documents",
        "GET /beacon/api/projects/{id:int}/imported-documents/{documentId:int}",
        "GET /beacon/api/projects/{id:int}/mcp-context",

        "GET /beacon/api/mcp/audit",
        "GET /beacon/api/mcp/learning-stats",
        "GET /beacon/api/mcp/learned-patterns",
        "GET /beacon/api/mcp/tools",
        "GET /beacon/api/mcp/settings",
        "GET /beacon/api/mcp/settings/locks",
        "GET /beacon/api/mcp/projects/{projectId:int}/settings",
        "PUT /beacon/api/mcp/projects/{projectId:int}/settings"
    };

    /// <summary>
    /// A <c>Finally</c> convention for the group <c>MapBeaconApi</c> is mapped in: an endpoint off the list answers 404;
    /// one on it requires <paramref name="hostPolicy"/> as well as Beacon's own policies, unless Beacon serves it to
    /// anonymous callers (<c>/auth/me</c> tells a signed-out shell where the host's sign-in is).
    /// </summary>
    public static void Apply(EndpointBuilder endpoint, string hostPolicy)
    {
        if (endpoint is not RouteEndpointBuilder route
            || route.RoutePattern.RawText is not { } pattern
            || !pattern.StartsWith(BeaconGatewayPaths.ApiPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (KeyOf(pattern, route.Metadata) is not { } key || !Allowed.Contains(key))
        {
            route.RequestDelegate = NotFound;
            route.Metadata.Add(new AllowAnonymousAttribute());
            return;
        }

        if (!route.Metadata.OfType<IAllowAnonymous>().Any())
        {
            route.Metadata.Add(new AuthorizeAttribute(hostPolicy));
        }
    }

    /// <summary><c>"METHOD /route/pattern"</c>, or null for an endpoint that is not one method on one route.</summary>
    public static string? KeyOf(string? pattern, IEnumerable<object> metadata)
    {
        var methods = metadata.OfType<IHttpMethodMetadata>().LastOrDefault()?.HttpMethods;

        return pattern != null && methods is [var method] ? $"{method} {pattern}" : null;
    }

    private static Task NotFound(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;

        return Task.CompletedTask;
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Beacon.Core;
using Beacon.Core.Mcp;

namespace Beacon.Api.Authentication;

/// <summary>
/// Middleware that redirects unauthenticated users to the login page.
/// Only active when login form is enabled, and only on Beacon's routes (<see cref="BeaconRequestPaths"/>).
/// </summary>
/// <remarks>
/// Which requests a signed-out browser reaches is the endpoints' metadata: an anonymous endpoint (the UI's login, logout,
/// error pages, the MCP discovery documents, the anonymous API routes) at the exact path it is mapped at, and an endpoint
/// with an authorization requirement, which <c>UseAuthorization</c> has already answered (the UI's pages through
/// <c>BeaconUiPage</c>, the API's 401). A request with no endpoint, or one whose endpoint carries neither, is redirected,
/// apart from the login and setup pages themselves, the API and MCP surfaces, the Warp dashboard and static files.
/// </remarks>
public sealed class LoginFormAuthMiddleware(
    RequestDelegate next,
    BeaconConfiguration configuration,
    string basePath)
{
    private readonly string _basePath = basePath.TrimEnd('/');

    public async Task InvokeAsync(HttpContext context)
    {
        // Host routes are the host's: never redirected to Beacon's login page.
        if (!configuration.Authentication.EnableLoginForm || !BeaconRequestPaths.IsBeaconRequest(context))
        {
            await next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated == true || IsAnsweredWithoutSignIn(context))
        {
            await next(context);
            return;
        }

        context.Response.Redirect(BeaconLoginPage.PathFor(_basePath, configuration));
    }

    private bool IsAnsweredWithoutSignIn(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<IAllowAnonymous>() != null)
        {
            if (IsAtItsMappedPath(endpoint, context.Request.Path))
            {
                return true;
            }
        }
        else if (endpoint != null && HasAuthorization(endpoint))
        {
            return true;
        }

        var path = context.Request.Path.Value ?? "";

        // The pages a signed-out browser is sent to: the login page (never redirected to itself) and, while Beacon manages
        // users, the first-run setup page.
        if (BeaconLoginPage.IsRequestFor(context.Request.Path, _basePath, configuration)
            || (configuration.UserManagement.Enabled
                && context.Request.Path.StartsWithSegments($"{_basePath}/setup", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // The REST API, the realtime hub and MCP answer a caller with a status (a 401 or a 404), never an HTML redirect.
        if (path.Contains("/api/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(McpDiscoveryPaths.McpPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The Warp dashboard enforces its own admin check. Match Warp's own case-sensitive (Ordinal) prefix test exactly:
        // a mixed-case path like /WARP that Warp never handles is redirected to the login page.
        if (path.StartsWith("/warp", StringComparison.Ordinal))
        {
            return true;
        }

        return MiddlewarePathHelper.IsStaticOrFrameworkPath(path);
    }

    // An anonymous route without parameters is anonymous at its mapped path only, compared Ordinal: the SPA's router and the
    // RFC 8615 well-known URIs are case-sensitive, so a differently cased variant (or one with a trailing slash) is
    // redirected to the login page instead of the SPA or a discovery document.
    private static bool IsAtItsMappedPath(Endpoint endpoint, PathString path)
    {
        if (endpoint is not RouteEndpoint { RoutePattern: { Parameters.Count: 0 } pattern })
        {
            return true;
        }

        var mapped = "/" + (pattern.RawText ?? "").TrimStart('/');

        return string.Equals(path.Value, mapped, StringComparison.Ordinal);
    }

    private static bool HasAuthorization(Endpoint endpoint)
    {
        return endpoint.Metadata.GetMetadata<IAuthorizeData>() != null
            || endpoint.Metadata.GetMetadata<AuthorizationPolicy>() != null
            || endpoint.Metadata.GetMetadata<IAuthorizationRequirementData>() != null;
    }
}

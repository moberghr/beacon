using Microsoft.AspNetCore.Http;
using Beacon.Core;
using Beacon.Core.Configuration;
using Beacon.Core.Mcp;

namespace Beacon.Api.Authentication;

/// <summary>
/// Middleware that redirects unauthenticated users to the login page.
/// Only active when login form is enabled, and only on Beacon's routes (<see cref="BeaconRequestPaths"/>).
/// </summary>
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

        // Allow access to login page, and static files
        if (IsAllowedPath(context.Request.Path, context.RequestServices?.GetService<AuthenticationPathPrefixes>()))
        {
            await next(context);
            return;
        }

        // Check if user is authenticated
        if (!context.User.Identity?.IsAuthenticated ?? true)
        {
            // Redirect to login page
            var loginPath = $"{_basePath}{configuration.Authentication.LoginPath}";
            context.Response.Redirect(loginPath);
            return;
        }

        await next(context);
    }

    private bool IsAllowedPath(PathString requestPath, AuthenticationPathPrefixes? prefixes)
    {
        var path = requestPath.Value ?? "";

        // Allow React auth landing pages (anonymous routes mounted under /beacon)
        if (path.Equals("/login", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/logout", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/error", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The same pages under the UI's base path (/beacon/login, …), including the login page this middleware
        // redirects to; otherwise a signed-out browser is redirected to the login page forever. Exact, Ordinal
        // matches: the SPA's router base path is case-sensitive, so a mixed-case variant redirects to login instead
        // of reaching the SPA fallback.
        var loginPath = configuration.Authentication.LoginPath;
        if (_basePath.Length > 0 &&
            ((loginPath.Length > 1 && path.Equals($"{_basePath}{loginPath}", StringComparison.Ordinal)) ||
             path.Equals($"{_basePath}/login", StringComparison.Ordinal) ||
             path.Equals($"{_basePath}/logout", StringComparison.Ordinal) ||
             path.Equals($"{_basePath}/error", StringComparison.Ordinal)))
        {
            return true;
        }

        // Allow setup page and API when user management is enabled (first-run scenario)
        if (configuration.UserManagement.Enabled &&
            MiddlewarePathHelper.IsUnderSegment(requestPath, _basePath, "/setup", prefixes))
        {
            return true;
        }

        // Allow all /api/ endpoints (REST API + SignalR hub + auth endpoints).
        // Endpoints opt into auth via .RequireAuthorization(); JSON 401 is returned
        // rather than an HTML redirect, which is wrong for JSON callers.
        if (MiddlewarePathHelper.IsUnderSegment(requestPath, _basePath, "/api", prefixes))
        {
            return true;
        }

        // Allow MCP endpoints (authenticated via API key or JWT)
        if (path.StartsWith("/beacon/mcp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Allow Warp dashboard (admin-only — enforced by dashboard authorization). Match Warp's own
        // case-sensitive (Ordinal) prefix test exactly: a case-insensitive match here would allow-list a
        // mixed-case path like /WARP that Warp never handles, letting it fall through to the SPA and serve
        // index.html to an anonymous caller instead of redirecting to login.
        if (path.StartsWith("/warp", StringComparison.Ordinal))
        {
            return true;
        }

        // Allow OpenAPI document
        if (path.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Allow the MCP discovery documents (RFC 9728 protected-resource metadata, MCP server
        // card) — anonymous by design so remote MCP clients can bootstrap OAuth discovery. ONLY
        // the exact mapped paths (shared constants, R6-2) are allow-listed: a /.well-known prefix
        // match would let /.well-known/anything reach the SPA fallback anonymously. Match Ordinal:
        // RFC 8615 well-known URIs are registered lowercase, and the mapped endpoints only answer
        // the exact-case path, so a mixed-case variant must fall through to the login redirect
        // instead of the SPA.
        if (McpDiscoveryPaths.AnonymousDiscoveryPaths.Contains(path, StringComparer.Ordinal))
        {
            return true;
        }

        return MiddlewarePathHelper.IsStaticOrFrameworkPath(path, _basePath, prefixes);
    }
}

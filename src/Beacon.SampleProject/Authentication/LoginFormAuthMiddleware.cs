using Microsoft.AspNetCore.Http;
using Beacon.Core;
using Beacon.MCP.Discovery;

namespace Beacon.SampleProject.Authentication;

/// <summary>
/// Middleware that redirects unauthenticated users to the login page.
/// Only active when login form is enabled.
/// </summary>
internal sealed class LoginFormAuthMiddleware(
    RequestDelegate next,
    BeaconConfiguration configuration,
    string basePath)
{
    private readonly string _basePath = basePath.TrimEnd('/');

    public async Task InvokeAsync(HttpContext context)
    {
        if (!configuration.Authentication.EnableLoginForm)
        {
            await next(context);
            return;
        }

        // Allow access to login page, static files, and Blazor endpoints
        if (IsAllowedPath(context.Request.Path))
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

    private bool IsAllowedPath(PathString requestPath)
    {
        var path = requestPath.Value ?? "";

        // Allow React auth landing pages (anonymous routes mounted at root)
        if (path.Equals("/login", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/logout", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/error", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Allow setup page and API when user management is enabled (first-run scenario)
        if (configuration.UserManagement.Enabled &&
            path.Contains("/setup", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Allow all /api/ endpoints (REST API + SignalR hub + auth endpoints).
        // Endpoints opt into auth via .RequireAuthorization(); JSON 401 is returned
        // rather than an HTML redirect, which is wrong for JSON callers.
        if (path.Contains("/api/", StringComparison.OrdinalIgnoreCase))
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
        if (McpDiscoveryEndpoints.AnonymousDiscoveryPaths.Contains(path, StringComparer.Ordinal))
        {
            return true;
        }

        return MiddlewarePathHelper.IsStaticOrFrameworkPath(path);
    }
}

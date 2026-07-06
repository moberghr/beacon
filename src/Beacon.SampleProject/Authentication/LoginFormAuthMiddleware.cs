using Microsoft.AspNetCore.Http;
using Beacon.Core;

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

        var path = context.Request.Path.Value ?? "";

        // Allow access to login page, static files, and Blazor endpoints
        if (IsAllowedPath(path))
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

    private bool IsAllowedPath(string path)
    {
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

        // Allow Hangfire dashboard (admin-only — enforced by dashboard authorization)
        if (path.StartsWith("/hangfire", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Allow OpenAPI document
        if (path.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return MiddlewarePathHelper.IsStaticOrFrameworkPath(path);
    }
}

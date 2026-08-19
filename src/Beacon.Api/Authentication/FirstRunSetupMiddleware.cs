using Microsoft.AspNetCore.Http;
using Beacon.Core;
using Beacon.Core.Services;
using Beacon.Core.Mcp;

namespace Beacon.Api.Authentication;

/// <summary>
/// Middleware that redirects to the setup page if no users exist (first-run scenario).
/// Only active when User Management is enabled.
/// </summary>
public sealed class FirstRunSetupMiddleware(
    RequestDelegate next,
    BeaconConfiguration configuration,
    string basePath)
{
    private readonly string _basePath = basePath.TrimEnd('/');

    public async Task InvokeAsync(HttpContext context, IUserManagementService userService)
    {
        // Skip if user management is not enabled
        if (!configuration.UserManagement.Enabled)
        {
            await next(context);
            return;
        }

        // Skip for setup page, static files, and framework endpoints
        if (IsExcludedPath(context.Request.Path))
        {
            await next(context);
            return;
        }

        // Check if this is first run (no users exist)
        var isFirstRun = await userService.IsFirstRunAsync(context.RequestAborted);
        if (isFirstRun)
        {
            // Redirect to setup page
            context.Response.Redirect($"{_basePath}/setup");
            return;
        }

        await next(context);
    }

    private static bool IsExcludedPath(PathString requestPath)
    {
        var path = requestPath.Value ?? "";
        if (path.Contains("/setup", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The MCP discovery documents are anonymous machine endpoints — a first-run HTML redirect
        // to /setup would corrupt them for remote MCP clients probing the server. ONLY the exact
        // mapped paths (shared constants, R6-2) are excluded, never all of /.well-known.
        if (McpDiscoveryPaths.AnonymousDiscoveryPaths.Contains(path, StringComparer.Ordinal))
        {
            return true;
        }

        return MiddlewarePathHelper.IsStaticOrFrameworkPath(path);
    }
}

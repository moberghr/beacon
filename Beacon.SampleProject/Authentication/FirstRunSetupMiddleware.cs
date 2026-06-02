using Microsoft.AspNetCore.Http;
using Beacon.Core;
using Beacon.Core.Services;

namespace Beacon.SampleProject.Authentication;

/// <summary>
/// Middleware that redirects to the setup page if no users exist (first-run scenario).
/// Only active when User Management is enabled.
/// </summary>
internal sealed class FirstRunSetupMiddleware(
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

        var path = context.Request.Path.Value ?? "";

        // Skip for setup page, static files, and framework endpoints
        if (IsExcludedPath(path))
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

    private static bool IsExcludedPath(string path)
    {
        if (path.Contains("/setup", StringComparison.OrdinalIgnoreCase))
            return true;

        return MiddlewarePathHelper.IsStaticOrFrameworkPath(path);
    }
}

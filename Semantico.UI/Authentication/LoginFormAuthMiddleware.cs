using Microsoft.AspNetCore.Http;
using Semantico.Core;

namespace Semantico.UI.Authentication;

/// <summary>
/// Middleware that redirects unauthenticated users to the login page.
/// Only active when login form is enabled.
/// </summary>
internal sealed class LoginFormAuthMiddleware(
    RequestDelegate next,
    SemanticoConfiguration configuration,
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
        // Allow login/logout pages
        var loginPath = configuration.Authentication.LoginPath.TrimStart('/');
        if (path.EndsWith($"/{loginPath}", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/logout", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return MiddlewarePathHelper.IsStaticOrFrameworkPath(path);
    }
}

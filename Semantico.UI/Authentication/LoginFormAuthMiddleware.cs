using Microsoft.AspNetCore.Http;
using Semantico.Core;

namespace Semantico.UI.Authentication;

/// <summary>
/// Middleware that redirects unauthenticated users to the login page.
/// Only active when login form is enabled.
/// </summary>
internal sealed class LoginFormAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SemanticoConfiguration _configuration;
    private readonly string _basePath;

    public LoginFormAuthMiddleware(
        RequestDelegate next,
        SemanticoConfiguration configuration,
        string basePath)
    {
        _next = next;
        _configuration = configuration;
        _basePath = basePath.TrimEnd('/');
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_configuration.Authentication.EnableLoginForm)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "";

        // Allow access to login page, static files, and Blazor endpoints
        if (IsAllowedPath(path))
        {
            await _next(context);
            return;
        }

        // Check if user is authenticated
        if (!context.User.Identity?.IsAuthenticated ?? true)
        {
            // Redirect to login page
            var loginPath = $"{_basePath}{_configuration.Authentication.LoginPath}";
            context.Response.Redirect(loginPath);
            return;
        }

        await _next(context);
    }

    private bool IsAllowedPath(string path)
    {
        // Allow login/logout pages
        var loginPath = _configuration.Authentication.LoginPath.TrimStart('/');
        if (path.EndsWith($"/{loginPath}", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/logout", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Allow static files
        if (path.Contains("/_content/") ||
            path.Contains("/_framework/") ||
            path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Allow Blazor negotiate endpoint
        if (path.Contains("/_blazor"))
        {
            return true;
        }

        return false;
    }
}

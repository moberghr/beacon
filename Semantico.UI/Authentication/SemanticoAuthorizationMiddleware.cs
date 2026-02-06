using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Semantico.Core;
using Semantico.Core.Authorization;

namespace Semantico.UI.Authentication;

internal sealed class SemanticoAuthorizationMiddleware(
    RequestDelegate next,
    ILogger<SemanticoAuthorizationMiddleware> logger)
{
    public async Task InvokeAsync(
        HttpContext context,
        ISemanticoAuthorizationProvider authorizationProvider,
        SemanticoConfiguration configuration)
    {
        // Skip if authorization disabled
        if (!configuration.Authorization.Enabled)
        {
            await next(context);
            return;
        }

        // Skip authorization for static files, Blazor framework, and API endpoints
        var path = context.Request.Path.Value ?? "";
        if (IsStaticFileOrFramework(path))
        {
            await next(context);
            return;
        }

        // Skip authorization for login/logout pages when login form is enabled
        if (configuration.Authentication.EnableLoginForm)
        {
            var loginPath = configuration.Authentication.LoginPath.TrimStart('/');
            if (path.EndsWith($"/{loginPath}", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/logout", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/api/auth/", StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }
        }

        // Determine if write operation
        var isWriteOperation = context.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE";

        // Check global permissions
        var hasPermission = isWriteOperation
            ? await authorizationProvider.HasWritePermissionAsync()
            : await authorizationProvider.HasReadPermissionAsync();

        if (!hasPermission)
        {
            logger.LogWarning(
                "Authorization denied for {Method} {Path}",
                context.Request.Method,
                context.Request.Path);

            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "Forbidden" });
            return;
        }

        await next(context);
    }

    private static bool IsStaticFileOrFramework(string path) =>
        MiddlewarePathHelper.IsStaticOrFrameworkPath(path);
}
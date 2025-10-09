using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Semantico.UI.AspNet.Authentication;

internal sealed class SemanticoAuthorizationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ISemanticoAuthorizationProvider _authorizationProvider;

    public SemanticoAuthorizationMiddleware(RequestDelegate next, ISemanticoAuthorizationProvider authorizationProvider)
    {
        _next = next;
        _authorizationProvider = authorizationProvider;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var username = context.User.Identity?.Name;

        if (string.IsNullOrEmpty(username))
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync("Forbidden: User not authenticated");
            return;
        }

        var isWriteOperation = IsWriteOperation(context.Request.Method);

        if (isWriteOperation)
        {
            var hasWritePermission = await _authorizationProvider.HasWritePermissionAsync(username);
            if (!hasWritePermission)
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("Forbidden: User does not have write permission");
                return;
            }
        }
        else
        {
            var hasReadPermission = await _authorizationProvider.HasReadPermissionAsync(username);
            if (!hasReadPermission)
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("Forbidden: User does not have read permission");
                return;
            }
        }

        await _next(context);
    }

    private static bool IsWriteOperation(string method)
    {
        return method is "POST" or "PUT" or "PATCH" or "DELETE";
    }
}
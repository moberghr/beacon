using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace Semantico.UI.Authentication;

/// <summary>
/// Explicitly authenticates requests using the cookie scheme within the Semantico pipeline.
/// This ensures Semantico routes use cookie auth without overriding the host app's default scheme.
/// </summary>
internal sealed class SemanticoCookieAuthMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var result = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (result.Succeeded)
        {
            context.User = result.Principal;
        }

        await next(context);
    }
}

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace Beacon.SampleProject.Authentication;

/// <summary>
/// Explicitly authenticates requests using the cookie scheme within the Beacon pipeline.
/// This ensures Beacon routes use cookie auth without overriding the host app's default scheme.
/// </summary>
internal sealed class BeaconCookieAuthMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // An earlier middleware (e.g. ApiKeyAuthMiddleware) may already have authenticated the
        // request with a directly-assigned principal — don't overwrite it (it carries scope claims).
        if (context.User.Identity?.IsAuthenticated == true)
        {
            await next(context);
            return;
        }

        var result = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (result.Succeeded)
        {
            context.User = result.Principal;
        }

        await next(context);
    }
}

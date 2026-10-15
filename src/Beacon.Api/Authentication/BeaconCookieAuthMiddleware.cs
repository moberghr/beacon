using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace Beacon.Api.Authentication;

/// <summary>
/// Explicitly authenticates requests using the cookie scheme within the Beacon pipeline.
/// This ensures Beacon routes use cookie auth without overriding the host app's default scheme. Only Beacon's routes
/// (<see cref="BeaconRequestPaths"/>): the cookie never authenticates a host route.
/// The cookie's result is recorded as the request's authentication result (<see cref="BeaconAuthenticationFeatures"/>),
/// the way the framework's authentication middleware records the default scheme's, so what reads it (SignalR closing a hub
/// connection when the session's expiry passes, a scheme-less authorization policy) sees the cookie session even where
/// another scheme (OIDC) is registered and the cookie is not the default.
/// </summary>
public sealed class BeaconCookieAuthMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // The Beacon.Auth cookie authenticates Beacon's routes only, never the host's own.
        if (!BeaconRequestPaths.IsBeaconRequest(context))
        {
            await next(context);
            return;
        }

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
            BeaconAuthenticationFeatures.Set(context, result);
        }

        await next(context);
    }
}

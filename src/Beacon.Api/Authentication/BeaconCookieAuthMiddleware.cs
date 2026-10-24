using Microsoft.AspNetCore.Http;

namespace Beacon.Api.Authentication;

/// <summary>
/// Retired: <c>UseBeaconAuthentication</c> authenticates the <c>Beacon.Auth</c> cookie in
/// <see cref="BeaconAuthenticationMiddleware"/>. Kept for hosts that wire it themselves; it behaves as before: on Beacon's
/// routes only (<see cref="BeaconRequestPaths"/>), and only when nothing authenticated the request earlier, the cookie's
/// result is recorded as the request's authentication result (<see cref="BeaconAuthenticationFeatures"/>).
/// </summary>
[Obsolete("Call app.UseBeaconAuthentication(), which authenticates the Beacon.Auth cookie; removed in 6.0.")]
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

        await BeaconAuthenticationMiddleware.AuthenticateCookieAsync(context);

        await next(context);
    }
}

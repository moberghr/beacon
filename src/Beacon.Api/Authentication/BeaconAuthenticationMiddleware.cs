using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace Beacon.Api.Authentication;

/// <summary>
/// The one point where <c>UseBeaconAuthentication</c> authenticates a Beacon request (§1.9), before the host's
/// <c>UseAuthentication</c> and <c>UseAuthorization</c>. On Beacon's routes only (<see cref="BeaconRequestPaths"/>, checked
/// first), and only when nothing authenticated the request earlier, it picks the scheme of the request's credential as
/// the <see cref="BeaconAuthenticationDefaults.AuthenticationScheme"/> policy scheme does and records the scheme's result
/// as the request's authentication result (<see cref="BeaconAuthenticationFeatures"/>), which the authorization
/// middleware and SignalR read (SignalR closes a hub connection when the result's expiry passes).
/// <list type="bullet">
/// <item>An API key or bearer token is judged by its handler, called directly, so the principal is not passed through the
/// host's claims transformations: a transformation never gives a scoped caller a role. A refused API key is answered
/// with a 401 problem on every Beacon route, also an anonymous one; a refused bearer token is answered on an API request
/// (<see cref="BeaconApiRequest"/>) and otherwise leaves the request to the cookie, so a page still reaches its session or
/// the login redirect.</item>
/// <item>Otherwise the <c>Beacon.Auth</c> cookie, through the authentication service (with the host's claims
/// transformations, as a sign-in's session needs its roles).</item>
/// </list>
/// </summary>
internal sealed class BeaconAuthenticationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // Beacon's credentials authenticate Beacon's routes only, never the host's own.
        if (!BeaconRequestPaths.IsBeaconRequest(context))
        {
            await next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            await next(context);
            return;
        }

        var scheme = BeaconAuthenticationDefaults.SelectScheme(context);
        if (scheme != CookieAuthenticationDefaults.AuthenticationScheme)
        {
            var handler = await GetHandlerAsync(context, scheme);
            var result = await handler.AuthenticateAsync();
            if (result.Succeeded)
            {
                BeaconAuthenticationFeatures.Set(context, result);
                await next(context);
                return;
            }

            if (result.Failure is BeaconCredentialRefusal { AnswersRequest: true })
            {
                await handler.ChallengeAsync(properties: null);
                return;
            }
        }

        await AuthenticateCookieAsync(context);

        await next(context);
    }

    /// <summary>Authenticates the <c>Beacon.Auth</c> cookie and records a session as the request's authentication result.</summary>
    internal static async Task AuthenticateCookieAsync(HttpContext context)
    {
        var result = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (result.Succeeded)
        {
            BeaconAuthenticationFeatures.Set(context, result);
        }
    }

    private static async Task<IAuthenticationHandler> GetHandlerAsync(HttpContext context, string scheme)
    {
        var handler = await context.RequestServices.GetRequiredService<IAuthenticationHandlerProvider>().GetHandlerAsync(context, scheme);

        return handler ?? throw new InvalidOperationException(
            $"The '{scheme}' authentication scheme is not registered: call services.AddBeaconCookieAuthentication() before app.UseBeaconAuthentication().");
    }
}

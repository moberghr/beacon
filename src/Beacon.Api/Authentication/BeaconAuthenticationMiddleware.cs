using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Beacon.Core.Authentication.Providers;
using Beacon.Core.Mcp;
using Beacon.Core.Services.Security;

namespace Beacon.Api.Authentication;

/// <summary>
/// The one point where <c>UseBeaconAuthentication</c> authenticates a Beacon request (§1.9), after the host's
/// <c>UseAuthentication</c> and before <c>UseAuthorization</c>. On Beacon's routes only (<see cref="BeaconRequestPaths"/>,
/// checked first) it picks the scheme of the request's credential as the
/// <see cref="BeaconAuthenticationDefaults.AuthenticationScheme"/> policy scheme does and records the result as the
/// request's authentication result (<see cref="BeaconAuthenticationFeatures"/>), which the authorization middleware and
/// SignalR read (SignalR closes a hub connection when the result's expiry passes).
/// <list type="bullet">
/// <item>A presented API key or bearer token decides, whatever authenticated the request before (a session cookie the
/// host's default scheme read): the caller gets the key's scope, projects and identity, never the session's rights. It is
/// judged by its handler, called directly (or by its authenticator, when the host registered no Beacon scheme), so the
/// principal is not passed through the host's claims transformations. A refused API key is answered with a 401 problem on
/// every Beacon route, also an anonymous one; a refused bearer token is answered on an API request
/// (<see cref="BeaconApiRequest"/>) and otherwise leaves the request to the cookie, so a page still reaches its session or
/// the login redirect.</item>
/// <item>Otherwise, when nothing authenticated the request yet, the <c>Beacon.Auth</c> cookie, through the authentication
/// service (with the host's claims transformations, as a sign-in's session needs its roles).</item>
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

        var scheme = BeaconAuthenticationDefaults.SelectScheme(context);
        if (scheme != CookieAuthenticationDefaults.AuthenticationScheme)
        {
            var result = await AuthenticateCredentialAsync(context, scheme);
            if (result.Succeeded)
            {
                BeaconAuthenticationFeatures.Set(context, result);
                await next(context);
                return;
            }

            var refusal = result.Failure as BeaconCredentialRefusal ?? BeaconCredentialRefusal.Recorded(context);
            if (refusal is { AnswersRequest: true })
            {
                await BeaconAuthenticationResponses.WriteRefusalAsync(context, refusal);
                return;
            }
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            await AuthenticateCookieAsync(context);
        }

        await next(context);
    }

    /// <summary>
    /// Authenticates the <c>Beacon.Auth</c> cookie and records a session as the request's authentication result. A host
    /// without the cookie scheme (its own sign-in, such as ASP.NET Identity) has no session to read: the request stays as
    /// the host's authentication left it.
    /// </summary>
    internal static async Task AuthenticateCookieAsync(HttpContext context)
    {
        var schemes = context.RequestServices.GetService<IAuthenticationSchemeProvider>();
        if (schemes != null && await schemes.GetSchemeAsync(CookieAuthenticationDefaults.AuthenticationScheme) == null)
        {
            return;
        }

        var result = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (result.Succeeded)
        {
            BeaconAuthenticationFeatures.Set(context, result);
        }
    }

    // The scheme's handler when the host registered Beacon's schemes (AddBeaconCookieAuthentication); otherwise the same
    // judgement straight from the authenticator, as before the schemes existed.
    private static async Task<AuthenticateResult> AuthenticateCredentialAsync(HttpContext context, string scheme)
    {
        var handler = await context.RequestServices.GetRequiredService<IAuthenticationHandlerProvider>().GetHandlerAsync(context, scheme);
        if (handler != null)
        {
            return await handler.AuthenticateAsync();
        }

        if (scheme == BeaconAuthenticationDefaults.ApiKeyScheme)
        {
            return await ApiKeyAuthenticator.AuthenticateAsync(
                context,
                context.RequestServices.GetRequiredService<IApiKeyService>(),
                context.RequestServices.GetRequiredService<ILogger<ApiKeyAuthenticationHandler>>());
        }

        var authenticator = context.RequestServices.GetService<BeaconJwtAuthenticator>();

        return authenticator == null
            ? AuthenticateResult.NoResult()
            : await authenticator.AuthenticateAsync(
                context,
                context.RequestServices.GetRequiredService<JwtExternalApiAuthenticationProvider>(),
                context.RequestServices.GetRequiredService<IMcpCallerMapper>());
    }
}

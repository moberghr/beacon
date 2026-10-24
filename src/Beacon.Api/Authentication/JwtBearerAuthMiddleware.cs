using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authentication;
using Beacon.Core.Authentication.Providers;
using Beacon.Core.Mcp;

namespace Beacon.Api.Authentication;

/// <summary>
/// The bearer token alone, for a pipeline that authenticates nothing else Beacon's way: the MCP gateway runs it on
/// <c>/beacon/mcp</c> before the host's own authentication (<c>UseBeaconJwtBearerAuthentication</c>).
/// <c>UseBeaconAuthentication</c> judges the token through <see cref="BeaconJwtAuthenticationHandler"/> instead. Both
/// share <see cref="BeaconJwtAuthenticator"/>, which describes what a token is accepted as. Outside Beacon's routes
/// (<see cref="BeaconRequestPaths"/>) the middleware does nothing: the host's own routes are the host's.
/// </summary>
internal sealed class JwtBearerAuthMiddleware(
    RequestDelegate next,
    JwtAuthenticationOptions options,
    ILogger<JwtBearerAuthMiddleware> logger)
{
    /// <summary>Authentication type of every identity this middleware builds.</summary>
    internal const string AuthenticationType = BeaconJwtAuthenticator.AuthenticationType;

    private readonly BeaconJwtAuthenticator _authenticator = new(options, logger);

    public async Task InvokeAsync(
        HttpContext context,
        JwtExternalApiAuthenticationProvider jwtProvider,
        IMcpCallerMapper callerMapper)
    {
        // A token Beacon accepts authenticates Beacon's routes only: a host route is neither authenticated nor refused here.
        if (!BeaconRequestPaths.IsBeaconRequest(context))
        {
            await next(context);
            return;
        }

        // Skip if user already authenticated (e.g., via cookie)
        if (context.User.Identity?.IsAuthenticated == true)
        {
            await next(context);
            return;
        }

        var result = await _authenticator.AuthenticateAsync(context, jwtProvider, callerMapper);
        if (result.Succeeded)
        {
            context.User = result.Principal!;
        }
        else if (result.Failure is BeaconCredentialRefusal { AnswersRequest: true } refusal)
        {
            await BeaconAuthenticationResponses.WriteRefusalAsync(context, refusal);
            return;
        }

        await next(context);
    }
}

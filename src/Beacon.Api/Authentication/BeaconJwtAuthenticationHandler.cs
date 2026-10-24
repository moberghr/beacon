using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Authentication.Providers;
using Beacon.Core.Mcp;

namespace Beacon.Api.Authentication;

/// <summary>
/// The <see cref="BeaconAuthenticationDefaults.BearerScheme"/> scheme: a JWT bearer token (<see cref="BeaconJwtAuthenticator"/>,
/// registered by <c>AddBeaconJwtAuthentication</c>), on Beacon's routes only (<see cref="BeaconRequestPaths"/>, checked
/// first). Without bearer authentication configured it authenticates nobody. Its challenge answers a refused token with
/// the refusal (401 with <c>WWW-Authenticate: Bearer error="invalid_token"</c>, or 503 while the signing keys cannot be
/// fetched), and its forbid is a 403 problem.
/// </summary>
internal sealed class BeaconJwtAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!BeaconRequestPaths.IsBeaconRequest(Context)
            || Context.RequestServices.GetService<BeaconJwtAuthenticator>() is not { Enabled: true } authenticator)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        return authenticator.AuthenticateAsync(
            Context,
            Context.RequestServices.GetRequiredService<JwtExternalApiAuthenticationProvider>(),
            Context.RequestServices.GetRequiredService<IMcpCallerMapper>());
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var result = await HandleAuthenticateOnceSafeAsync();
        if (result.Failure is BeaconCredentialRefusal refusal)
        {
            await BeaconAuthenticationResponses.WriteRefusalAsync(Context, refusal);
            return;
        }

        Response.Headers.WWWAuthenticate = "Bearer";
        await BeaconAuthenticationResponses.WriteProblemAsync(Context, StatusCodes.Status401Unauthorized);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        return BeaconAuthenticationResponses.WriteProblemAsync(Context, StatusCodes.Status403Forbidden);
    }
}

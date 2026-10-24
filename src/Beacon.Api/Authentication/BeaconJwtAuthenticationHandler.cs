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
/// first). Without bearer authentication configured it authenticates nobody. A refused token is no result with the
/// refusal kept on the request (<see cref="BeaconCredentialRefusal.Record"/>); the challenge answers a refused token with
/// the refusal (401 with <c>WWW-Authenticate: Bearer error="invalid_token"</c>, or 503 while the signing keys cannot be
/// fetched), and its forbid is a 403 problem.
/// </summary>
internal sealed class BeaconJwtAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!BeaconRequestPaths.IsBeaconRequest(Context)
            || Context.RequestServices.GetService<BeaconJwtAuthenticator>() is not { Enabled: true } authenticator)
        {
            return AuthenticateResult.NoResult();
        }

        var result = await authenticator.AuthenticateAsync(
            Context,
            Context.RequestServices.GetRequiredService<JwtExternalApiAuthenticationProvider>(),
            Context.RequestServices.GetRequiredService<IMcpCallerMapper>());

        return BeaconCredentialRefusal.Record(Context, result);
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        await HandleAuthenticateOnceSafeAsync();
        if (BeaconCredentialRefusal.Recorded(Context) is { } refusal)
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

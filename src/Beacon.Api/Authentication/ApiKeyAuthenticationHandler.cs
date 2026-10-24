using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Services.Security;

namespace Beacon.Api.Authentication;

/// <summary>
/// The <see cref="BeaconAuthenticationDefaults.ApiKeyScheme"/> scheme: a Beacon API key (<see cref="ApiKeyAuthenticator"/>),
/// on Beacon's routes only (<see cref="BeaconRequestPaths"/>, checked first) — on a host route a key is neither accepted
/// nor refused. A refused key is no result with the refusal kept on the request (<see cref="BeaconCredentialRefusal.Record"/>);
/// the challenge answers with it (401 problem), and the forbid is a 403 problem.
/// </summary>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!BeaconRequestPaths.IsBeaconRequest(Context))
        {
            return AuthenticateResult.NoResult();
        }

        var result = await ApiKeyAuthenticator.AuthenticateAsync(Context, Context.RequestServices.GetRequiredService<IApiKeyService>(), Logger);

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

        await BeaconAuthenticationResponses.WriteProblemAsync(Context, StatusCodes.Status401Unauthorized);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        return BeaconAuthenticationResponses.WriteProblemAsync(Context, StatusCodes.Status403Forbidden, AuthServiceExtensions.ApiKeyRefusalDetail(Context));
    }
}

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
/// nor refused. Its challenge answers a refused key with the refusal (401 problem), and its forbid is a 403 problem.
/// </summary>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!BeaconRequestPaths.IsBeaconRequest(Context))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        return ApiKeyAuthenticator.AuthenticateAsync(Context, Context.RequestServices.GetRequiredService<IApiKeyService>(), Logger);
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var result = await HandleAuthenticateOnceSafeAsync();
        if (result.Failure is BeaconCredentialRefusal refusal)
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

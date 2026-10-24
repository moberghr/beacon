using Microsoft.AspNetCore.Http;
using Beacon.Core.Services.Security;

namespace Beacon.Api.Authentication;

/// <summary>
/// Retired: <c>UseBeaconAuthentication</c> judges the API key through <see cref="ApiKeyAuthenticationHandler"/>. Kept for
/// hosts that wire it themselves; it behaves as before (<see cref="ApiKeyAuthenticator"/>).
/// </summary>
[Obsolete("Call app.UseBeaconAuthentication(), which authenticates API keys through the Beacon authentication scheme; removed in 6.0.")]
public sealed class ApiKeyAuthMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IApiKeyService apiKeyService, ILogger<ApiKeyAuthMiddleware> logger)
    {
        // A Beacon API key authenticates Beacon's routes only, never the host's own.
        if (!BeaconRequestPaths.IsBeaconRequest(context))
        {
            await next(context);
            return;
        }

        // Skip if already authenticated
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            await next(context);
            return;
        }

        var result = await ApiKeyAuthenticator.AuthenticateAsync(context, apiKeyService, logger);
        if (result.Failure is BeaconCredentialRefusal refusal)
        {
            await BeaconAuthenticationResponses.WriteRefusalAsync(context, refusal);
            return;
        }

        if (result.Succeeded)
        {
            BeaconAuthenticationFeatures.Set(context, result);
        }

        await next(context);
    }
}

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Beacon.Core;
using Beacon.Core.Authentication;

namespace Beacon.Api.Endpoints;

/// <summary>
/// SSO / OIDC challenge endpoint. Companion to <see cref="LoginEndpoints"/>;
/// both are wired through <c>MapLoginEndpoints</c>.
/// </summary>
public static partial class LoginEndpoints
{
    private static void MapSsoChallenge(
        IEndpointRouteBuilder endpoints,
        string basePath,
        BeaconConfiguration configuration)
    {
        var loginRedirectFallback = configuration.Authentication.LoginRedirectPath;
        endpoints.MapGet($"{basePath}/api/auth/sso/challenge", (
            HttpContext context,
            string? returnUrl,
            IOptions<OidcAuthenticationOptions> oidcOptions) =>
        {
            if (!oidcOptions.Value.Enabled)
            {
                return Results.NotFound();
            }

            var safeReturnUrl = IsSafeReturnUrl(returnUrl, basePath)
                ? returnUrl!
                : loginRedirectFallback;

            var properties = new AuthenticationProperties
            {
                RedirectUri = safeReturnUrl,
                IsPersistent = true
            };

            return Results.Challenge(properties, new[] { OpenIdConnectDefaults.AuthenticationScheme });
        }).AllowAnonymous();
    }

    internal static bool IsSafeReturnUrl(string? returnUrl, string basePath)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return false;
        }

        if (!returnUrl.StartsWith('/'))
        {
            return false;
        }

        if (returnUrl.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        if (returnUrl.StartsWith("/\\", StringComparison.Ordinal))
        {
            return false;
        }

        var normalizedBase = basePath.TrimEnd('/');
        if (string.IsNullOrEmpty(normalizedBase))
        {
            return true;
        }

        return returnUrl.Equals(normalizedBase, StringComparison.OrdinalIgnoreCase)
            || returnUrl.StartsWith($"{normalizedBase}/", StringComparison.OrdinalIgnoreCase);
    }
}

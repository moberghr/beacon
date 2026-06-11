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

    /// <summary>
    /// Accepts only local, root-relative URLs (starts with "/", not "//" or "/\", no scheme).
    /// UI routes live at the root, so there is no base-path prefix requirement — any other
    /// shape (absolute URL, protocol-relative, backslash trick) is rejected to prevent open
    /// redirects. <paramref name="basePath"/> is retained for signature compatibility only.
    /// </summary>
    internal static bool IsSafeReturnUrl(string? returnUrl, string basePath)
    {
        _ = basePath;
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

        // Defense in depth: must parse as a relative URI (rejects anything carrying a scheme).
        return Uri.TryCreate(returnUrl, UriKind.Relative, out _);
    }
}

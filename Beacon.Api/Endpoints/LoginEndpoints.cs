using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Beacon.Core;
using Beacon.Core.Authentication;

namespace Beacon.Api.Endpoints;

/// <summary>
/// React SPA login endpoints — issue and clear the <c>Beacon.Auth</c> cookie.
/// </summary>
public static class LoginEndpoints
{
    public static IEndpointRouteBuilder MapLoginEndpoints(
        this IEndpointRouteBuilder endpoints,
        string basePath,
        BeaconConfiguration configuration)
    {
        // POST /beacon/api/auth/login
        endpoints.MapPost($"{basePath}/api/auth/login", async (
            HttpContext context,
            IBeaconAuthenticationProvider authProvider,
            LoginRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.BadRequest(new LoginResponse { Success = false, Error = "Username and password are required." });
            }

            var result = await authProvider.AuthenticateAsync(request.Username, request.Password);

            if (!result.Success || result.User == null)
            {
                var failurePayload = new LoginResponse { Success = false, Error = result.ErrorMessage ?? "Invalid credentials." };
                return Results.Json(failurePayload, statusCode: StatusCodes.Status401Unauthorized);
            }

            // Build claims
            var claims = result.User.ToClaims();
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);

            var authProperties = new AuthenticationProperties
            {
                IsPersistent = request.RememberMe,
                ExpiresUtc = request.RememberMe
                    ? DateTimeOffset.UtcNow.AddDays(configuration.Authentication.RememberMeExpirationDays)
                    : DateTimeOffset.UtcNow.AddHours(configuration.Authentication.CookieExpirationHours),
                AllowRefresh = true
            };

            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, authProperties);

            // LoginRedirectPath is an absolute UI path — the React shell is
            // mounted at root, not under the API base path.
            return Results.Ok(new LoginResponse
            {
                Success = true,
                RedirectUrl = configuration.Authentication.LoginRedirectPath
            });
        })
        .AllowAnonymous()
        .RequireRateLimiting("login")
        .DisableAntiforgery();

        // POST /beacon/api/auth/logout
        endpoints.MapPost($"{basePath}/api/auth/logout", async (HttpContext context, IBeaconAuthenticationProvider authProvider) =>
        {
            await authProvider.SignOutAsync();
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

            return Results.Ok(new { success = true });
        }).AllowAnonymous();

        // GET /beacon/api/auth/signout — browser-navigable signout that clears the cookie and redirects
        var loginPath = $"{basePath}{configuration.Authentication.LoginPath}";
        endpoints.MapGet($"{basePath}/api/auth/signout", async (HttpContext context, IBeaconAuthenticationProvider authProvider) =>
        {
            await authProvider.SignOutAsync();
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

            return Results.Redirect(loginPath);
        }).AllowAnonymous();

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

        return endpoints;
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

public record LoginRequest(string? Username, string? Password, bool RememberMe = false);

public record LoginResponse
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? RedirectUrl { get; init; }
}

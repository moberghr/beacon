using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Beacon.Core;
using Beacon.Core.Authentication;

namespace Beacon.Api.Endpoints;

/// <summary>
/// React SPA login endpoints — issue and clear the <c>Beacon.Auth</c> cookie.
/// SSO/OIDC challenge endpoints live in <c>SsoEndpoints.cs</c>; both are
/// wired by the single <see cref="MapLoginEndpoints"/> entry point.
/// </summary>
public static partial class LoginEndpoints
{
    public static IEndpointRouteBuilder MapLoginEndpoints(
        this IEndpointRouteBuilder endpoints,
        string basePath,
        BeaconConfiguration configuration)
    {
        MapCookieAuth(endpoints, basePath, configuration);
        MapSsoChallenge(endpoints, basePath, configuration);
        return endpoints;
    }

    private static void MapCookieAuth(
        IEndpointRouteBuilder endpoints,
        string basePath,
        BeaconConfiguration configuration)
    {
        // POST /beacon/api/auth/login
        endpoints.MapPost($"{basePath}/api/auth/login", async (
            HttpContext context,
            IBeaconAuthenticationProvider authProvider,
            IAntiforgery antiforgery,
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

            // Antiforgery tokens are bound to claims; the cookie issued
            // pre-login was minted for the anonymous user and won't validate
            // against the now-authenticated identity. Reissue here so the
            // first authenticated POST doesn't 400 with a "different claims"
            // mismatch.
            context.User = principal;
            antiforgery.SetCookieTokenAndHeader(context);

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
        endpoints.MapPost($"{basePath}/api/auth/logout", async (
            HttpContext context,
            IBeaconAuthenticationProvider authProvider,
            IAntiforgery antiforgery) =>
        {
            await authProvider.SignOutAsync();
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            // Reissue the antiforgery cookie for the now-anonymous user so a
            // subsequent login doesn't carry over a stale identity-bound token.
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
            antiforgery.SetCookieTokenAndHeader(context);

            return Results.Ok(new { success = true });
        }).AllowAnonymous();

        // GET /beacon/api/auth/signout — browser-navigable signout that clears the cookie and redirects
        var loginPath = $"{basePath}{configuration.Authentication.LoginPath}";
        endpoints.MapGet($"{basePath}/api/auth/signout", async (
            HttpContext context,
            IBeaconAuthenticationProvider authProvider,
            IAntiforgery antiforgery) =>
        {
            await authProvider.SignOutAsync();
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
            antiforgery.SetCookieTokenAndHeader(context);

            return Results.Redirect(loginPath);
        }).AllowAnonymous();
    }
}

public record LoginRequest(string? Username, string? Password, bool RememberMe = false);

public record LoginResponse
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? RedirectUrl { get; init; }
}

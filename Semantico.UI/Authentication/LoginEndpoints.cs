using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Semantico.Core;
using Semantico.Core.Authentication;

namespace Semantico.UI.Authentication;

/// <summary>
/// HTTP endpoints for login/logout that properly handle cookies.
/// Blazor Server cannot set cookies via SignalR, so we need real HTTP endpoints.
/// </summary>
public static class LoginEndpoints
{
    public static IEndpointRouteBuilder MapLoginEndpoints(
        this IEndpointRouteBuilder endpoints,
        string basePath,
        SemanticoConfiguration configuration)
    {
        // POST /semantico/api/auth/login
        endpoints.MapPost($"{basePath}/api/auth/login", async (
            HttpContext context,
            ISemanticoAuthenticationProvider authProvider,
            LoginRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.BadRequest(new LoginResponse { Success = false, Error = "Username and password are required." });
            }

            var result = await authProvider.AuthenticateAsync(request.Username, request.Password);

            if (!result.Success || result.User == null)
            {
                return Results.Ok(new LoginResponse { Success = false, Error = result.ErrorMessage ?? "Invalid credentials." });
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

            var redirectPath = configuration.Authentication.LoginRedirectPath;
            return Results.Ok(new LoginResponse
            {
                Success = true,
                RedirectUrl = $"{basePath}{redirectPath}"
            });
        }).AllowAnonymous();

        // POST /semantico/api/auth/logout
        endpoints.MapPost($"{basePath}/api/auth/logout", async (HttpContext context, ISemanticoAuthenticationProvider authProvider) =>
        {
            await authProvider.SignOutAsync();
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

            return Results.Ok(new { success = true });
        }).AllowAnonymous();

        // GET /semantico/api/auth/signout — browser-navigable signout that clears the cookie and redirects
        var loginPath = $"{basePath}{configuration.Authentication.LoginPath}";
        endpoints.MapGet($"{basePath}/api/auth/signout", async (HttpContext context, ISemanticoAuthenticationProvider authProvider) =>
        {
            await authProvider.SignOutAsync();
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

            return Results.Redirect(loginPath);
        }).AllowAnonymous();

        return endpoints;
    }
}

public record LoginRequest(string? Username, string? Password, bool RememberMe = false);

public record LoginResponse
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? RedirectUrl { get; init; }
}

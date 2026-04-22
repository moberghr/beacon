using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Beacon.Core;
using Beacon.Core.Authentication;

namespace Beacon.UI.Authentication;

/// <summary>
/// Implementation that calls the authentication provider and manages ASP.NET Core cookie authentication.
/// </summary>
public class BeaconAuthenticationService : IBeaconAuthenticationService
{
    private readonly IBeaconAuthenticationProvider _authenticationProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly BeaconConfiguration _configuration;

    public BeaconAuthenticationService(
        IBeaconAuthenticationProvider authenticationProvider,
        IHttpContextAccessor httpContextAccessor,
        BeaconConfiguration configuration)
    {
        _authenticationProvider = authenticationProvider;
        _httpContextAccessor = httpContextAccessor;
        _configuration = configuration;
    }

    public bool IsAuthenticated =>
        _httpContextAccessor.HttpContext?.User?.Identity?.IsAuthenticated ?? false;

    public string? CurrentUserName =>
        _httpContextAccessor.HttpContext?.User?.Identity?.Name;

    public async Task<AuthenticationResult> SignInAsync(
        string username,
        string password,
        bool rememberMe = false,
        CancellationToken cancellationToken = default)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            return AuthenticationResult.Failed("HTTP context is not available.");
        }

        // Call the authentication provider
        var result = await _authenticationProvider.AuthenticateAsync(username, password, cancellationToken);

        if (!result.Success || result.User == null)
        {
            return result;
        }

        // Build claims from the authenticated user
        var claims = result.User.ToClaims();
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        // Configure authentication properties
        var authProperties = new AuthenticationProperties
        {
            IsPersistent = rememberMe,
            ExpiresUtc = rememberMe
                ? DateTimeOffset.UtcNow.AddDays(_configuration.Authentication.RememberMeExpirationDays)
                : DateTimeOffset.UtcNow.AddHours(_configuration.Authentication.CookieExpirationHours),
            AllowRefresh = true
        };

        await httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            authProperties);

        return result;
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            return;
        }

        // Call the provider's sign out (for any cleanup)
        await _authenticationProvider.SignOutAsync(cancellationToken);

        // Sign out from cookie authentication
        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}

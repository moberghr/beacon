using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Authorization.ProjectAccess;

namespace Beacon.Api.Authentication;

/// <summary>
/// Implementation that calls the authentication provider and manages ASP.NET Core cookie authentication. While local login
/// is off (<c>Beacon:UserManagement:LocalLogin:Enabled</c> false) it signs in only a stored internal break-glass user, as
/// the login endpoint does.
/// </summary>
public class BeaconAuthenticationService : IBeaconAuthenticationService
{
    private const string InvalidCredentials = "Invalid username or password.";

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

        // Local login off: as at the login endpoint, only a stored internal break-glass user signs in with a password,
        // whichever provider authenticated it.
        var localLogin = _configuration.UserManagement.LocalLogin;
        if (!localLogin.Enabled)
        {
            var breakGlassUserId = await PasswordSignInGate.BreakGlassUserIdAsync(httpContext.RequestServices, result, cancellationToken);
            var logger = httpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger<BeaconAuthenticationService>();
            if (breakGlassUserId == null)
            {
                logger?.LogInformation(
                    ProjectAccessEvents.PasswordSignInRefused,
                    "Password sign-in refused: local login is off and the account is not a break-glass account");

                return AuthenticationResult.Failed(InvalidCredentials);
            }

            logger?.LogWarning(
                ProjectAccessEvents.BreakGlassSignIn,
                "Break-glass password sign-in while local login is off (user {UserId})",
                breakGlassUserId);
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

        // The user was disabled or archived, or their security stamp changed, between the password check and the sign-in:
        // the cookie carries no session.
        return BeaconSessionEvents.WasSignInRefused(httpContext)
            ? AuthenticationResult.Failed(InvalidCredentials)
            : result;
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

using Beacon.Core.Authentication;

namespace Beacon.Core;

/// <summary>
/// Configuration options for authentication.
/// </summary>
public class AuthenticationOptions
{
    /// <summary>
    /// Enable login form. Default: false (backward compatible)
    /// </summary>
    public bool EnableLoginForm { get; set; } = false;

    /// <summary>
    /// Authentication provider type. If null, uses DefaultAuthenticationProvider (which fails).
    /// </summary>
    public Type? ProviderType { get; set; }

    /// <summary>
    /// Path to redirect to after successful login (local sign-in and the SSO fallback). The browser
    /// reaches it with a full page load, outside the SPA router, so this is the full path including
    /// the <c>/beacon</c> mount point the React shell is served under.
    /// Default: "/beacon/home"
    /// </summary>
    public string LoginRedirectPath { get; set; } = "/beacon/home";

    /// <summary>
    /// Path to the login page.
    /// Default: "/login"
    /// </summary>
    public string LoginPath { get; set; } = "/login";

    /// <summary>
    /// Cookie expiration in hours for normal login.
    /// Default: 24 hours
    /// </summary>
    public int CookieExpirationHours { get; set; } = 24;

    /// <summary>
    /// Enable "Remember Me" functionality on login form.
    /// Default: true
    /// </summary>
    public bool EnableRememberMe { get; set; } = true;

    /// <summary>
    /// Cookie expiration in days when "Remember Me" is checked.
    /// Default: 30 days
    /// </summary>
    public int RememberMeExpirationDays { get; set; } = 30;

    /// <summary>
    /// JWT authentication configuration.
    /// Configure for external JWT API authentication (login form flow)
    /// and/or bearer token authentication (stateless API access).
    /// </summary>
    public JwtAuthenticationOptions? Jwt { get; set; }

    public OidcAuthenticationOptions? Oidc { get; set; }
}

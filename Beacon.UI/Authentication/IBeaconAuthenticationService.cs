using Beacon.Core.Authentication;

namespace Beacon.UI.Authentication;

/// <summary>
/// Service that coordinates between the authentication provider and ASP.NET Core cookie authentication.
/// </summary>
public interface IBeaconAuthenticationService
{
    /// <summary>
    /// Authenticates the user and creates a cookie session.
    /// </summary>
    /// <param name="username">The username.</param>
    /// <param name="password">The password.</param>
    /// <param name="rememberMe">Whether to persist the cookie across browser sessions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The authentication result.</returns>
    Task<AuthenticationResult> SignInAsync(
        string username,
        string password,
        bool rememberMe = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs out the current user by clearing the cookie session.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SignOutAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets whether the current user is authenticated.
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// Gets the current username if authenticated.
    /// </summary>
    string? CurrentUserName { get; }
}

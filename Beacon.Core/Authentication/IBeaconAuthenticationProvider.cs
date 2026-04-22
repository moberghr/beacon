namespace Beacon.Core.Authentication;

/// <summary>
/// Provides authentication decisions for Beacon.
/// Implement this interface to integrate with your authentication system
/// (e.g., database, LDAP, OAuth, etc.).
/// </summary>
public interface IBeaconAuthenticationProvider
{
    /// <summary>
    /// Authenticates a user with the given credentials.
    /// </summary>
    /// <param name="username">The username or email.</param>
    /// <param name="password">The password.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The authentication result containing success status and user details.</returns>
    Task<AuthenticationResult> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates whether a user session is still valid.
    /// </summary>
    /// <param name="userId">The user ID to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the session is valid, false otherwise.</returns>
    Task<bool> ValidateSessionAsync(
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs out the current user (optional cleanup).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SignOutAsync(CancellationToken cancellationToken = default);
}

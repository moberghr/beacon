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
    /// Validates whether a user session is still valid. Beacon does not call it: with user management on, sessions are
    /// revalidated against the user store; without it, only their absolute lifetime applies, and a host that needs more
    /// revalidates in its own cookie events.
    /// </summary>
    /// <param name="userId">The user ID to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the session is valid, false otherwise.</returns>
    [Obsolete("Beacon never calls ValidateSessionAsync. Sessions are revalidated against the user store while Beacon user management is on (Beacon:Authentication:Session, BeaconSessionEvents); a host without it revalidates in its own cookie authentication events.")]
    Task<bool> ValidateSessionAsync(
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs out the current user (optional cleanup).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SignOutAsync(CancellationToken cancellationToken = default);
}

namespace Beacon.Core.Authorization;

/// <summary>
/// Provides access to the current authenticated user's context.
/// Implementations integrate with any authentication system.
/// </summary>
public interface IBeaconUserContext
{
    /// <summary>
    /// Current user's unique identifier (null if not authenticated).
    /// </summary>
    string? UserId { get; }

    /// <summary>
    /// Current user's login username.
    /// </summary>
    string? UserName { get; }

    /// <summary>
    /// Current user's display name (may differ from username).
    /// </summary>
    string? DisplayName { get; }

    /// <summary>
    /// Current user's email address.
    /// </summary>
    string? Email { get; }

    /// <summary>
    /// All claims for the current user.
    /// </summary>
    IEnumerable<string> Claims { get; }

    /// <summary>
    /// Checks if user has a specific claim.
    /// </summary>
    bool HasClaim(string claimType, string? claimValue = null);

    /// <summary>
    /// Custom metadata from authentication provider.
    /// </summary>
    IDictionary<string, object> Metadata { get; }

    /// <summary>
    /// Whether a user is authenticated.
    /// </summary>
    bool IsAuthenticated { get; }
}

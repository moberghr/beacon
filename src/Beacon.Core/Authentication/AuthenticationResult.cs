using System.Security.Claims;

namespace Beacon.Core.Authentication;

/// <summary>
/// Represents the result of an authentication attempt.
/// </summary>
public class AuthenticationResult
{
    /// <summary>
    /// Indicates whether the authentication was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Error message when authentication fails.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// The authenticated user details when successful.
    /// </summary>
    public AuthenticatedUser? User { get; init; }

    /// <summary>
    /// The validated token's raw claims (unmapped names such as <c>oid</c>, multi-valued <c>groups</c>), set only by
    /// bearer-token validation. <see cref="AuthenticatedUser.Claims"/> keeps just the first value per type.
    /// </summary>
    public ClaimsPrincipal? TokenPrincipal { get; init; }

    /// <summary>
    /// The validated token's JOSE header <c>typ</c> (for example <c>at+jwt</c>), set only by bearer-token validation.
    /// The header is not among <see cref="TokenPrincipal"/>'s claims.
    /// </summary>
    public string? TokenType { get; init; }

    /// <summary>
    /// The Beacon user id the result is bound to, when the provider bound one (the login-form JWT flow binds the token
    /// to an existing Beacon user). A provider that wraps another re-reads exactly this user, never one found by a
    /// provider-agnostic external id.
    /// </summary>
    public int? BeaconUserId { get; init; }

    /// <summary>
    /// Creates a failed authentication result with an error message.
    /// </summary>
    public static AuthenticationResult Failed(string message) =>
        new() { Success = false, ErrorMessage = message };

    /// <summary>
    /// Creates a successful authentication result with user details.
    /// </summary>
    public static AuthenticationResult Succeeded(AuthenticatedUser user) =>
        new() { Success = true, User = user };
}

/// <summary>
/// Represents an authenticated user with their claims and roles.
/// </summary>
public class AuthenticatedUser
{
    /// <summary>
    /// Unique identifier for the user.
    /// </summary>
    public required string UserId { get; init; }

    /// <summary>
    /// The username used for authentication.
    /// </summary>
    public required string UserName { get; init; }

    /// <summary>
    /// The user's email address (optional).
    /// </summary>
    public string? Email { get; init; }

    /// <summary>
    /// Display name for the user (optional).
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Roles assigned to the user.
    /// </summary>
    public IEnumerable<string> Roles { get; init; } = [];

    /// <summary>
    /// Additional claims for the user (key-value pairs).
    /// </summary>
    public IDictionary<string, string> Claims { get; init; } = new Dictionary<string, string>();
}

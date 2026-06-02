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

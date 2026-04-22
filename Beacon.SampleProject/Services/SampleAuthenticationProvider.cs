using Beacon.Core.Authentication;

namespace Beacon.SampleProject.Services;

/// <summary>
/// Sample authentication provider demonstrating how to implement custom authentication logic.
/// This provider uses in-memory users for demo purposes.
/// </summary>
public class SampleAuthenticationProvider : IBeaconAuthenticationProvider
{
    // In-memory users for demonstration
    private static readonly Dictionary<string, (string Password, string[] Roles)> Users = new(StringComparer.OrdinalIgnoreCase)
    {
        ["admin"] = ("admin", new[] { "Admin", "Editor", "Viewer" }),
        ["editor"] = ("editor", new[] { "Editor", "Viewer" }),
        ["viewer"] = ("viewer", new[] { "Viewer" })
    };

    public Task<AuthenticationResult> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return Task.FromResult(AuthenticationResult.Failed("Username and password are required."));
        }

        if (!Users.TryGetValue(username, out var user))
        {
            return Task.FromResult(AuthenticationResult.Failed("Invalid username or password."));
        }

        if (!user.Password.Equals(password, StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticationResult.Failed("Invalid username or password."));
        }

        var authenticatedUser = new AuthenticatedUser
        {
            UserId = username.ToLowerInvariant(),
            UserName = username,
            DisplayName = GetDisplayName(username),
            Email = $"{username.ToLowerInvariant()}@example.com",
            Roles = user.Roles
        };

        return Task.FromResult(AuthenticationResult.Succeeded(authenticatedUser));
    }

    public Task<bool> ValidateSessionAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        // For this demo, session is always valid if user exists
        return Task.FromResult(Users.ContainsKey(userId));
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        // No cleanup needed for in-memory users
        return Task.CompletedTask;
    }

    private static string GetDisplayName(string username)
    {
        return username.ToLowerInvariant() switch
        {
            "admin" => "Administrator",
            "editor" => "Content Editor",
            "viewer" => "Read-Only User",
            _ => username
        };
    }
}

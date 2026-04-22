using Beacon.Core.Services;

namespace Beacon.Core.Authentication.Providers;

/// <summary>
/// Authentication provider that authenticates users against the Beacon database.
/// For internal users with passwords stored in Beacon.
/// </summary>
public class DatabaseAuthenticationProvider(IUserManagementService userService) : IBeaconAuthenticationProvider
{
    public async Task<AuthenticationResult> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        return await userService.AuthenticateInternalUserAsync(username, password, cancellationToken);
    }

    public async Task<bool> ValidateSessionAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var user = await userService.GetUserByExternalIdAsync(userId, cancellationToken);
        return user != null && user.IsEnabled;
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        // No additional cleanup needed for database authentication
        return Task.CompletedTask;
    }
}

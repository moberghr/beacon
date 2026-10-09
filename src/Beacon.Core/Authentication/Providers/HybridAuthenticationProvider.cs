using Beacon.Core.Services;

namespace Beacon.Core.Authentication.Providers;

/// <summary>
/// Authentication provider that combines internal database authentication with external JWT authentication.
/// Tries internal authentication first, then falls back to JWT if configured.
/// </summary>
public class HybridAuthenticationProvider : IBeaconAuthenticationProvider
{
    private readonly IUserManagementService _userService;
    private readonly JwtExternalApiAuthenticationProvider? _jwtProvider;

    public HybridAuthenticationProvider(
        IUserManagementService userService,
        JwtExternalApiAuthenticationProvider? jwtProvider = null)
    {
        _userService = userService;
        _jwtProvider = jwtProvider;
    }

    public async Task<AuthenticationResult> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        // Try internal authentication first
        var internalResult = await _userService.AuthenticateInternalUserAsync(username, password, cancellationToken);
        if (internalResult.Success)
        {
            return internalResult;
        }

        // If JWT provider is configured, try external authentication
        if (_jwtProvider != null)
        {
            var jwtResult = await _jwtProvider.AuthenticateAsync(username, password, cancellationToken);
            if (!jwtResult.Success || jwtResult.User == null)
            {
                return jwtResult;
            }

            // The JWT provider already bound the token to one Beacon user (and recorded the login); re-read exactly that
            // user by id, never by an external id that another account (an internal one included) could share.
            var user = jwtResult.BeaconUserId is { } userId
                ? await _userService.GetUserByIdAsync(userId, cancellationToken)
                : null;
            if (user == null || !user.IsEnabled)
            {
                return AuthenticationResult.Failed("Invalid username or password.");
            }

            return new AuthenticationResult
            {
                Success = true,
                BeaconUserId = user.Id,
                User = new AuthenticatedUser
                {
                    UserId = user.ExternalId,
                    UserName = user.UserName,
                    Email = user.Email,
                    DisplayName = user.DisplayName,
                    Roles = user.Roles
                        .Select(x => x.Name)
                        .ToList()
                }
            };
        }

        // Return the internal auth error
        return internalResult;
    }

    public async Task<bool> ValidateSessionAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var user = await _userService.GetUserByExternalIdAsync(userId, cancellationToken);
        return user != null && user.IsEnabled;
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        // No additional cleanup needed
        return Task.CompletedTask;
    }
}

using Beacon.Core.Services;

namespace Beacon.Core.Authentication.Providers;

/// <summary>
/// Authentication provider that combines internal database authentication with external JWT authentication.
/// Tries internal authentication first, then falls back to JWT if configured. The fallback is password sign-in too: it is
/// off while local login is off (<see cref="LocalLoginOptions"/>), and never taken for an internal user who must choose a
/// new password.
/// </summary>
public class HybridAuthenticationProvider : IBeaconAuthenticationProvider
{
    private readonly IUserManagementService _userService;
    private readonly JwtExternalApiAuthenticationProvider? _jwtProvider;
    private readonly BeaconConfiguration? _configuration;

    public HybridAuthenticationProvider(
        IUserManagementService userService,
        JwtExternalApiAuthenticationProvider? jwtProvider = null,
        BeaconConfiguration? configuration = null)
    {
        _userService = userService;
        _jwtProvider = jwtProvider;
        _configuration = configuration;
    }

    public async Task<AuthenticationResult> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        // Try internal authentication first
        var internalResult = await _userService.AuthenticateInternalUserAsync(username, password, cancellationToken);
        if (internalResult.Success || internalResult.PasswordChangeRequired)
        {
            return internalResult;
        }

        // If JWT provider is configured, try external authentication: only while local login is known to be on (without
        // Beacon's configuration it is not known, and the fallback is not taken).
        if (_jwtProvider != null && _configuration?.UserManagement.LocalLogin.Enabled == true)
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

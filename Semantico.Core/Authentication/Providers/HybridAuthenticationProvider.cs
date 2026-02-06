using Semantico.Core.Services;

namespace Semantico.Core.Authentication.Providers;

/// <summary>
/// Authentication provider that combines internal database authentication with external JWT authentication.
/// Tries internal authentication first, then falls back to JWT if configured.
/// </summary>
public class HybridAuthenticationProvider : ISemanticoAuthenticationProvider
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
            if (jwtResult.Success && jwtResult.User != null)
            {
                // Check if this external user is pre-registered in Semantico
                var user = await _userService.GetUserByExternalIdAsync(jwtResult.User.UserId, cancellationToken);
                if (user == null)
                {
                    // External user is not pre-registered
                    return AuthenticationResult.Failed("User is not registered in this system. Please contact an administrator.");
                }

                if (!user.IsEnabled)
                {
                    return AuthenticationResult.Failed("This account has been disabled.");
                }

                // Update last login
                await _userService.UpdateLastLoginAsync(user.ExternalId, cancellationToken);

                // Return authenticated user with roles from Semantico database
                return AuthenticationResult.Succeeded(new AuthenticatedUser
                {
                    UserId = user.ExternalId,
                    UserName = user.UserName,
                    Email = user.Email ?? jwtResult.User.Email,
                    DisplayName = user.DisplayName ?? jwtResult.User.DisplayName,
                    Roles = user.Roles.Select(r => r.Name),
                    Claims = jwtResult.User.Claims
                });
            }

            // Return the JWT error if internal auth also failed
            return jwtResult;
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

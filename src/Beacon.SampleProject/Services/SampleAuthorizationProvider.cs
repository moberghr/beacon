using Beacon.Core.Authorization;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;

namespace Beacon.SampleProject.Services;

/// <summary>
/// Sample authorization provider demonstrating how to implement custom authorization logic.
/// Read is allowed for any enabled user; write requires Editor role or higher. Roles are
/// resolved from the Beacon user store — never inferred from the username string — so
/// self-registration with a privileged-looking username cannot grant permissions.
/// </summary>
public class SampleAuthorizationProvider(
    IBeaconUserContext userContext,
    IUserManagementService userService) : IBeaconAuthorizationProvider
{
    private BeaconUserData? _cachedUser;
    private bool _userFetched;

    public async Task<bool> HasReadPermissionAsync(CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        if (user == null || !user.IsEnabled)
        {
            return false;
        }

        if (user.IsSuperAdmin)
        {
            return true;
        }

        return user.Roles.Any(x => x.Level >= RoleService.RoleLevels.Viewer);
    }

    public async Task<bool> HasWritePermissionAsync(CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        if (user == null || !user.IsEnabled)
        {
            return false;
        }

        if (user.IsSuperAdmin)
        {
            return true;
        }

        return user.Roles.Any(x => x.Level >= RoleService.RoleLevels.Editor);
    }

    private async Task<BeaconUserData?> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        if (_userFetched)
        {
            return _cachedUser;
        }

        var externalId = userContext.UserId;
        if (string.IsNullOrEmpty(externalId))
        {
            _userFetched = true;
            return null;
        }

        _cachedUser = await userService.GetUserByExternalIdAsync(externalId, cancellationToken);
        _userFetched = true;
        return _cachedUser;
    }
}

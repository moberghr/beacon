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

    public Task<AuthorizationResult?> AuthorizeAsync(
        ResourceType resourceType,
        int resourceId,
        PermissionAction action,
        CancellationToken cancellationToken = default)
    {
        // Return null to use global permissions only (no resource-level checks)
        return Task.FromResult<AuthorizationResult?>(null);
    }

    public Task<AuthorizationResult?> AuthorizeNewResourceAsync(
        ResourceType resourceType,
        PermissionAction action,
        object? resourceContext = null,
        CancellationToken cancellationToken = default)
    {
        // Return null to use global permissions only (no resource-level checks)
        return Task.FromResult<AuthorizationResult?>(null);
    }

    public Task<IEnumerable<int>?> GetAccessibleResourceIdsAsync(
        ResourceType resourceType,
        PermissionAction action,
        CancellationToken cancellationToken = default)
    {
        // Return null = user sees all resources (no filtering)
        return Task.FromResult<IEnumerable<int>?>(null);
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

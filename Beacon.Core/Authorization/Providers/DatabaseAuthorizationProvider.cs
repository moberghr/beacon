using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;

namespace Beacon.Core.Authorization.Providers;

/// <summary>
/// Authorization provider that looks up user roles from the Beacon database.
/// Provides role-based authorization: Admin (level 3), Editor (level 2), Viewer (level 1).
/// </summary>
public class DatabaseAuthorizationProvider(
    IBeaconUserContext userContext,
    IUserManagementService userService) : IBeaconAuthorizationProvider
{
    private BeaconUserData? _cachedUser;
    private bool _userFetched;

    /// <summary>
    /// Viewers and above can read.
    /// </summary>
    public async Task<bool> HasReadPermissionAsync(CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        if (user == null || !user.IsEnabled)
            return false;

        // SuperAdmins always have access
        if (user.IsSuperAdmin)
            return true;

        // Viewers (level 1) and above can read
        return user.Roles.Any(r => r.Level >= RoleService.RoleLevels.Viewer);
    }

    /// <summary>
    /// Editors and above can write.
    /// </summary>
    public async Task<bool> HasWritePermissionAsync(CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        if (user == null || !user.IsEnabled)
            return false;

        // SuperAdmins always have access
        if (user.IsSuperAdmin)
            return true;

        // Editors (level 2) and above can write
        return user.Roles.Any(r => r.Level >= RoleService.RoleLevels.Editor);
    }

    /// <summary>
    /// Resource-level authorization based on roles.
    /// </summary>
    public async Task<AuthorizationResult?> AuthorizeAsync(
        ResourceType resourceType,
        int resourceId,
        PermissionAction action,
        CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        if (user == null || !user.IsEnabled)
            return AuthorizationResult.Failure("Access denied.");

        // SuperAdmins can do everything
        if (user.IsSuperAdmin)
            return AuthorizationResult.Success();

        var maxLevel = user.Roles.Any() ? user.Roles.Max(r => r.Level) : 0;

        return action switch
        {
            PermissionAction.Read => maxLevel >= RoleService.RoleLevels.Viewer
                ? AuthorizationResult.Success()
                : AuthorizationResult.Failure("Viewer role or higher required."),

            PermissionAction.Create or PermissionAction.Update or PermissionAction.Execute =>
                maxLevel >= RoleService.RoleLevels.Editor
                    ? AuthorizationResult.Success()
                    : AuthorizationResult.Failure("Editor role or higher required."),

            PermissionAction.Delete or PermissionAction.Archive =>
                maxLevel >= RoleService.RoleLevels.Admin
                    ? AuthorizationResult.Success()
                    : AuthorizationResult.Failure("Admin role required."),

            PermissionAction.Approve or PermissionAction.Lock =>
                maxLevel >= RoleService.RoleLevels.Admin
                    ? AuthorizationResult.Success()
                    : AuthorizationResult.Failure("Admin role required."),

            PermissionAction.Export =>
                maxLevel >= RoleService.RoleLevels.Viewer
                    ? AuthorizationResult.Success()
                    : AuthorizationResult.Failure("Viewer role or higher required."),

            _ => AuthorizationResult.Failure("Unknown action.")
        };
    }

    /// <summary>
    /// Authorization for new resources (before ID is assigned).
    /// </summary>
    public async Task<AuthorizationResult?> AuthorizeNewResourceAsync(
        ResourceType resourceType,
        PermissionAction action,
        object? resourceContext = null,
        CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        if (user == null || !user.IsEnabled)
            return AuthorizationResult.Failure("Access denied.");

        // SuperAdmins can do everything
        if (user.IsSuperAdmin)
            return AuthorizationResult.Success();

        var maxLevel = user.Roles.Any() ? user.Roles.Max(r => r.Level) : 0;

        // Creating new resources requires Editor level
        if (action == PermissionAction.Create)
        {
            return maxLevel >= RoleService.RoleLevels.Editor
                ? AuthorizationResult.Success()
                : AuthorizationResult.Failure("Editor role or higher required to create resources.");
        }

        return null; // Defer to other checks
    }

    /// <summary>
    /// Gets accessible resource IDs. Returns null (no filtering) for role-based auth.
    /// </summary>
    public Task<IEnumerable<int>?> GetAccessibleResourceIdsAsync(
        ResourceType resourceType,
        PermissionAction action,
        CancellationToken cancellationToken = default)
    {
        // Role-based authorization doesn't filter by specific resource IDs
        // All users with appropriate role level can see all resources
        return Task.FromResult<IEnumerable<int>?>(null);
    }

    private async Task<BeaconUserData?> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        if (_userFetched)
            return _cachedUser;

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

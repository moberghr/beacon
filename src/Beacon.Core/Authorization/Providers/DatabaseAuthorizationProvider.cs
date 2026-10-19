using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using Microsoft.AspNetCore.Http;

namespace Beacon.Core.Authorization.Providers;

/// <summary>
/// Authorization provider that looks up user roles from the Beacon database.
/// Provides role-based authorization: Admin (level 3), Editor (level 2), Viewer (level 1).
/// </summary>
public class DatabaseAuthorizationProvider(
    IHttpContextAccessor? httpContextAccessor,
    IUserManagementService userService) : IBeaconAuthorizationProvider
{
    private BeaconUserData? _cachedUser;
    private bool _userFetched;

    /// <summary>For a host without Beacon.Api (a worker): no HTTP context accessor, so there is never a request's caller.</summary>
    public DatabaseAuthorizationProvider(IUserManagementService userService)
        : this(null, userService)
    {
    }

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
        return user.Roles.Any(x => x.Level >= RoleService.RoleLevels.Viewer);
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
        return user.Roles.Any(x => x.Level >= RoleService.RoleLevels.Editor);
    }

    private async Task<BeaconUserData?> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        if (_userFetched)
        {
            return _cachedUser;
        }

        _cachedUser = await FindCurrentUserAsync(cancellationToken);
        _userFetched = true;
        return _cachedUser;
    }

    // Resolved as BeaconUserLookup names the user: an API key by its username claim (a user-less key resolves to
    // nobody), every other principal by NameIdentifier to Users.ExternalId.
    private Task<BeaconUserData?> FindCurrentUserAsync(CancellationToken cancellationToken)
    {
        var lookup = BeaconUserLookup.Of(httpContextAccessor?.HttpContext?.User);
        if (lookup.UserName != null)
        {
            return userService.GetUserByUserNameAsync(lookup.UserName, cancellationToken);
        }

        return lookup.ExternalId != null
            ? userService.GetUserByExternalIdAsync(lookup.ExternalId, cancellationToken)
            : Task.FromResult<BeaconUserData?>(null);
    }
}

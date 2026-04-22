namespace Beacon.Core.Authorization.Providers;

/// <summary>
/// Simple role-based authorization.
/// Roles: Admin (full access), Editor (read/write), Viewer (read-only)
/// </summary>
public class RoleBasedAuthorizationProvider : IBeaconAuthorizationProvider
{
    private readonly IBeaconUserContext _userContext;

    public RoleBasedAuthorizationProvider(IBeaconUserContext userContext)
    {
        _userContext = userContext;
    }

    public Task<bool> HasReadPermissionAsync(CancellationToken cancellationToken = default)
    {
        var role = GetUserRole();
        return Task.FromResult(role is BeaconRole.Admin or BeaconRole.Editor or BeaconRole.Viewer);
    }

    public Task<bool> HasWritePermissionAsync(CancellationToken cancellationToken = default)
    {
        var role = GetUserRole();
        return Task.FromResult(role is BeaconRole.Admin or BeaconRole.Editor);
    }

    public Task<AuthorizationResult?> AuthorizeAsync(
        ResourceType resourceType, int resourceId, PermissionAction action,
        CancellationToken cancellationToken = default)
    {
        var role = GetUserRole();

        // Admin has full access
        if (role == BeaconRole.Admin)
            return Task.FromResult<AuthorizationResult?>(AuthorizationResult.Success());

        var allowed = action switch
        {
            PermissionAction.Read => role is BeaconRole.Editor or BeaconRole.Viewer,
            PermissionAction.Execute => role is BeaconRole.Editor or BeaconRole.Viewer,
            PermissionAction.Export => role is BeaconRole.Editor or BeaconRole.Viewer,
            PermissionAction.Create => role == BeaconRole.Editor,
            PermissionAction.Update => role == BeaconRole.Editor,
            PermissionAction.Archive => role == BeaconRole.Editor,
            PermissionAction.Approve => role == BeaconRole.Editor,
            PermissionAction.Lock => role == BeaconRole.Editor,
            PermissionAction.Delete => false, // Only admin can delete
            _ => false
        };

        return Task.FromResult<AuthorizationResult?>(
            allowed
                ? AuthorizationResult.Success()
                : AuthorizationResult.Failure($"Role '{role}' cannot {action} {resourceType}"));
    }

    public Task<AuthorizationResult?> AuthorizeNewResourceAsync(
        ResourceType resourceType, PermissionAction action, object? resourceContext = null,
        CancellationToken cancellationToken = default)
    {
        return AuthorizeAsync(resourceType, 0, action, cancellationToken);
    }

    public Task<IEnumerable<int>?> GetAccessibleResourceIdsAsync(
        ResourceType resourceType, PermissionAction action,
        CancellationToken cancellationToken = default)
    {
        // In simple RBAC, users see all or nothing
        var role = GetUserRole();
        var hasAccess = role is BeaconRole.Admin or BeaconRole.Editor or BeaconRole.Viewer;
        return Task.FromResult<IEnumerable<int>?>(hasAccess ? null : Array.Empty<int>());
    }

    private BeaconRole GetUserRole()
    {
        if (!_userContext.IsAuthenticated)
            return BeaconRole.Guest;

        // Check for role claim
        if (_userContext.HasClaim(BeaconClaims.Role, "Admin"))
            return BeaconRole.Admin;
        if (_userContext.HasClaim(BeaconClaims.Role, "Editor"))
            return BeaconRole.Editor;
        if (_userContext.HasClaim(BeaconClaims.Role, "Viewer"))
            return BeaconRole.Viewer;

        // Default: Viewer
        return BeaconRole.Viewer;
    }
}

public enum BeaconRole
{
    Guest = 0,
    Viewer = 1,
    Editor = 2,
    Admin = 3
}

namespace Semantico.Core.Authorization.Providers;

/// <summary>
/// Simple role-based authorization.
/// Roles: Admin (full access), Editor (read/write), Viewer (read-only)
/// </summary>
public class RoleBasedAuthorizationProvider : ISemanticoAuthorizationProvider
{
    private readonly ISemanticoUserContext _userContext;

    public RoleBasedAuthorizationProvider(ISemanticoUserContext userContext)
    {
        _userContext = userContext;
    }

    public Task<bool> HasReadPermissionAsync(CancellationToken cancellationToken = default)
    {
        var role = GetUserRole();
        return Task.FromResult(role is SemanticoRole.Admin or SemanticoRole.Editor or SemanticoRole.Viewer);
    }

    public Task<bool> HasWritePermissionAsync(CancellationToken cancellationToken = default)
    {
        var role = GetUserRole();
        return Task.FromResult(role is SemanticoRole.Admin or SemanticoRole.Editor);
    }

    public Task<AuthorizationResult?> AuthorizeAsync(
        ResourceType resourceType, int resourceId, PermissionAction action,
        CancellationToken cancellationToken = default)
    {
        var role = GetUserRole();

        // Admin has full access
        if (role == SemanticoRole.Admin)
            return Task.FromResult<AuthorizationResult?>(AuthorizationResult.Success());

        var allowed = action switch
        {
            PermissionAction.Read => role is SemanticoRole.Editor or SemanticoRole.Viewer,
            PermissionAction.Execute => role is SemanticoRole.Editor or SemanticoRole.Viewer,
            PermissionAction.Export => role is SemanticoRole.Editor or SemanticoRole.Viewer,
            PermissionAction.Create => role == SemanticoRole.Editor,
            PermissionAction.Update => role == SemanticoRole.Editor,
            PermissionAction.Archive => role == SemanticoRole.Editor,
            PermissionAction.Approve => role == SemanticoRole.Editor,
            PermissionAction.Lock => role == SemanticoRole.Editor,
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
        var hasAccess = role is SemanticoRole.Admin or SemanticoRole.Editor or SemanticoRole.Viewer;
        return Task.FromResult<IEnumerable<int>?>(hasAccess ? null : Array.Empty<int>());
    }

    private SemanticoRole GetUserRole()
    {
        if (!_userContext.IsAuthenticated)
            return SemanticoRole.Guest;

        // Check for role claim
        if (_userContext.HasClaim(SemanticoClaims.Role, "Admin"))
            return SemanticoRole.Admin;
        if (_userContext.HasClaim(SemanticoClaims.Role, "Editor"))
            return SemanticoRole.Editor;
        if (_userContext.HasClaim(SemanticoClaims.Role, "Viewer"))
            return SemanticoRole.Viewer;

        // Default: Viewer
        return SemanticoRole.Viewer;
    }
}

public enum SemanticoRole
{
    Guest = 0,
    Viewer = 1,
    Editor = 2,
    Admin = 3
}

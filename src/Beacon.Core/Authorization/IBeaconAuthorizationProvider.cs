namespace Beacon.Core.Authorization;

/// <summary>
/// Provides authorization decisions for Beacon resources.
/// </summary>
public interface IBeaconAuthorizationProvider
{
    // ===== GLOBAL PERMISSIONS (backward compatible with existing interface) =====

    Task<bool> HasReadPermissionAsync(CancellationToken cancellationToken = default);
    Task<bool> HasWritePermissionAsync(CancellationToken cancellationToken = default);

    // ===== RESOURCE-LEVEL PERMISSIONS (new, optional) =====

    /// <summary>
    /// Checks if current user can perform action on a specific resource.
    /// Return null to skip check (use global permissions only).
    /// </summary>
    Task<AuthorizationResult?> AuthorizeAsync(
        ResourceType resourceType,
        int resourceId,
        PermissionAction action,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if current user can perform action on a new resource (before ID assigned).
    /// Return null to skip check (use global permissions only).
    /// </summary>
    Task<AuthorizationResult?> AuthorizeNewResourceAsync(
        ResourceType resourceType,
        PermissionAction action,
        object? resourceContext = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets resource IDs the user can access.
    /// Return null if no filtering needed (user sees all).
    /// Return empty list if user sees nothing.
    /// </summary>
    Task<IEnumerable<int>?> GetAccessibleResourceIdsAsync(
        ResourceType resourceType,
        PermissionAction action,
        CancellationToken cancellationToken = default);
}

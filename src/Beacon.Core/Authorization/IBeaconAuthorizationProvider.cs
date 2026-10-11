namespace Beacon.Core.Authorization;

/// <summary>
/// The global read and write permission of the request's caller (Viewer and Editor), which caps its project access.
/// Which projects and resources a caller reaches is decided by <see cref="IProjectAccessEvaluator"/>.
/// </summary>
public interface IBeaconAuthorizationProvider
{
    /// <summary>The message of the resource-level members, which Beacon never calls.</summary>
    public const string ResourceLevelObsoleteMessage =
        "Not called by Beacon. Project and resource access is decided by IProjectAccessEvaluator; removed in 5.0.";

    Task<bool> HasReadPermissionAsync(CancellationToken cancellationToken = default);
    Task<bool> HasWritePermissionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the caller, who holds the functional permission <paramref name="permission"/> (a
    /// <see cref="BeaconPermissions"/> name) and is not an Admin, may use it on this request. Asked on the routes the
    /// permission opens, instead of <see cref="HasReadPermissionAsync"/> and <see cref="HasWritePermissionAsync"/>, which an
    /// Admin is still asked: a host vetoes a permission here. True by default.
    /// </summary>
    Task<bool> AllowsFunctionalPermissionAsync(string permission, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    [Obsolete(ResourceLevelObsoleteMessage)]
    Task<AuthorizationResult?> AuthorizeAsync(
        ResourceType resourceType,
        int resourceId,
        PermissionAction action,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<AuthorizationResult?>(null);

    [Obsolete(ResourceLevelObsoleteMessage)]
    Task<AuthorizationResult?> AuthorizeNewResourceAsync(
        ResourceType resourceType,
        PermissionAction action,
        object? resourceContext = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<AuthorizationResult?>(null);

    [Obsolete(ResourceLevelObsoleteMessage)]
    Task<IEnumerable<int>?> GetAccessibleResourceIdsAsync(
        ResourceType resourceType,
        PermissionAction action,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IEnumerable<int>?>(null);
}

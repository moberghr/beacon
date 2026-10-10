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

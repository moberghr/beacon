namespace Semantico.Core.Authorization.Providers;

/// <summary>
/// Default provider that allows all operations (backward compatible).
/// Used when authorization is disabled or no provider configured.
/// </summary>
internal sealed class DefaultAuthorizationProvider : ISemanticoAuthorizationProvider
{
    public Task<bool> HasReadPermissionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task<bool> HasWritePermissionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task<AuthorizationResult?> AuthorizeAsync(
        ResourceType resourceType, int resourceId, PermissionAction action,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<AuthorizationResult?>(null); // Skip resource checks

    public Task<AuthorizationResult?> AuthorizeNewResourceAsync(
        ResourceType resourceType, PermissionAction action, object? resourceContext = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<AuthorizationResult?>(null); // Skip resource checks

    public Task<IEnumerable<int>?> GetAccessibleResourceIdsAsync(
        ResourceType resourceType, PermissionAction action,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IEnumerable<int>?>(null); // No filtering
}

namespace Beacon.Core.Authorization.Providers;

/// <summary>
/// Default provider that allows all operations (backward compatible).
/// Used when authorization is disabled or no provider configured.
/// </summary>
internal sealed class DefaultAuthorizationProvider : IBeaconAuthorizationProvider
{
    public Task<bool> HasReadPermissionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task<bool> HasWritePermissionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}

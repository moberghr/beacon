using Beacon.Core.Authorization;

namespace Beacon.SampleProject.Services;

/// <summary>
/// Sample authorization provider demonstrating how to implement custom authorization logic.
/// This provider allows all read operations but restricts write operations to admins.
/// </summary>
public class SampleAuthorizationProvider : IBeaconAuthorizationProvider
{
    private readonly IBeaconUserContext _userContext;

    public SampleAuthorizationProvider(IBeaconUserContext userContext)
    {
        _userContext = userContext;
    }

    public Task<bool> HasReadPermissionAsync(CancellationToken cancellationToken = default)
    {
        // All authenticated users can read
        return Task.FromResult(_userContext.IsAuthenticated);
    }

    public Task<bool> HasWritePermissionAsync(CancellationToken cancellationToken = default)
    {
        // Only admin users can write
        return Task.FromResult(
            _userContext.IsAuthenticated &&
            _userContext.UserName?.Equals("admin", StringComparison.OrdinalIgnoreCase) == true);
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
}

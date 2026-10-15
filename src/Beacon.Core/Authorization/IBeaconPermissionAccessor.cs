namespace Beacon.Core.Authorization;

/// <summary>
/// The functional permissions of the current request's caller, read live: a stored user's roles in the user store now
/// (and, with <c>Beacon:Authorization:IdpRoles:Enabled</c>, the permissions its sign-in mapped from identity-provider
/// app roles); on a host without Beacon user management, the session's role claims. An API key, an MCP caller and work
/// without a request hold none. Scoped and resolved once per request. Registered with <c>TryAdd</c>, so a host can
/// replace it.
/// </summary>
public interface IBeaconPermissionAccessor
{
    Task<BeaconPermissionSet> GetCurrentAsync(CancellationToken cancellationToken);
}

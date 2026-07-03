namespace Beacon.Core.Authorization;

/// <summary>
/// Resolves the current authenticated principal's internal <c>BeaconUser.Id</c> (int) from
/// the external id claim (<c>ClaimTypes.NameIdentifier</c>). Mutating endpoints feed this
/// value into audit columns (§1.7 / §9.5) so the audit trail is never null. Returns null if
/// the claim is absent or no matching user row exists.
/// </summary>
public interface IActorUserResolver
{
    Task<int?> ResolveActorUserIdAsync(CancellationToken cancellationToken = default);
}

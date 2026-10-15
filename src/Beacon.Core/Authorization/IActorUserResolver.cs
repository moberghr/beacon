namespace Beacon.Core.Authorization;

/// <summary>
/// Resolves the current authenticated principal's internal <c>BeaconUser.Id</c> (int): an API key's owner by the
/// numeric user id the key carries, any other principal by its external id claim (<c>ClaimTypes.NameIdentifier</c>),
/// as <see cref="BeaconActor"/> resolves the caller. Mutating endpoints feed this value into audit columns (§1.7 / §9.5)
/// so the audit trail is never null. Returns null if the claim is absent or no matching user row exists.
/// </summary>
public interface IActorUserResolver
{
    Task<int?> ResolveActorUserIdAsync(CancellationToken cancellationToken = default);
}

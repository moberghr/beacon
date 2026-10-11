namespace Beacon.Core.Authorization;

/// <summary>
/// The <see cref="BeaconActor"/> of the current request: who owns, creates and works resources, and whether the caller
/// is an Admin. Scoped; the caller is resolved once per request. In a scope that runs as the owner of scheduled work
/// (<see cref="IRunAsScope"/>), the caller is that owner.
/// </summary>
public interface IBeaconActorAccessor
{
    Task<BeaconActor> GetCurrentAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The owner of scheduled work as an actor the project access evaluator decides for: <paramref name="ownerUserId"/>
    /// is the <c>Users.ExternalId</c> a subscription, contract or AI actor recorded. Null resolves to an ownerless
    /// owner. Never the current request's caller; hosts are not expected to implement it.
    /// </summary>
    Task<BeaconActor> ResolveOwnerAsync(string? ownerUserId, CancellationToken cancellationToken);
}

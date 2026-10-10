namespace Beacon.Core.Authorization;

/// <summary>
/// The <see cref="BeaconActor"/> of the current request: who owns, creates and works resources, and whether the caller
/// is an Admin. Scoped; the caller is resolved once per request.
/// </summary>
public interface IBeaconActorAccessor
{
    Task<BeaconActor> GetCurrentAsync(CancellationToken cancellationToken);
}

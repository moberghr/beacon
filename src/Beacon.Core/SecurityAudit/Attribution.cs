using Beacon.Core.Authorization;
using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.SecurityAudit;

/// <summary>
/// Who changed an attributed resource (<see cref="IAttributedEntity"/>): always the request's caller as Beacon names it
/// (<see cref="BeaconActor.UserId"/>, a <c>Users.ExternalId</c>), never an id a request carries. A resource's creator is
/// set where it is built, from the same actor.
/// </summary>
public static class Attribution
{
    /// <summary>Records <paramref name="actor"/> as the last to change <paramref name="entity"/>, now.</summary>
    public static void RecordChangeBy(this IAttributedEntity entity, BeaconActor actor, TimeProvider timeProvider)
    {
        entity.RecordChangeBy(actor.UserId, timeProvider);
    }

    /// <summary>
    /// Records the caller whose <see cref="BeaconActor.UserId"/> is <paramref name="actorUserId"/> as the last to change
    /// <paramref name="entity"/>, now: for a service handed the caller's id by the handler that resolved the caller.
    /// </summary>
    public static void RecordChangeBy(this IAttributedEntity entity, string? actorUserId, TimeProvider timeProvider)
    {
        entity.ModifiedByUserId = actorUserId;
        entity.ModifiedTime = timeProvider.GetUtcNow().UtcDateTime;
    }
}

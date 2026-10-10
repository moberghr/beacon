using Microsoft.Extensions.Logging;

namespace Beacon.Core.Authorization;

/// <summary>
/// A refused owner, assignee or Admin check on a resource. Logged at Warning with the resource and the caller's user id
/// only. A security audit event for refused checks belongs here.
/// </summary>
internal static class AccessRefusal
{
    public static UnauthorizedAccessException Of(
        ILogger logger,
        BeaconActor actor,
        string resource,
        int? resourceId,
        string message)
    {
        logger.LogWarning(
            "Refused a change to {Resource} {ResourceId} by user {UserId}",
            resource,
            resourceId,
            actor.UserId);

        return new UnauthorizedAccessException(message);
    }
}

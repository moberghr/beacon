using Microsoft.Extensions.Logging;

namespace Beacon.Core.Authorization;

/// <summary>
/// Who may change the owner of scheduled work (a subscription, a data contract, an AI actor): an Admin or a UserAdmin, by
/// the roles they hold now. A UserAdmin who is not an Admin never makes themselves the owner: the work would then run as
/// them, and they could change it as its owner. Refusals are 403, logged with ids only.
/// </summary>
internal static class OwnerTransfer
{
    public const string SelfMessage = "A UserAdmin cannot make themselves the owner; an Admin can.";

    /// <summary>
    /// Refuses the change of <paramref name="resource"/> <paramref name="resourceId"/>'s owner to user
    /// <paramref name="newOwnerUserId"/> (a <c>Users.Id</c>) by a caller who may not make it.
    /// </summary>
    public static void Demand(
        BeaconPermissionSet permissions,
        BeaconActor actor,
        int newOwnerUserId,
        string resource,
        int resourceId,
        string notAllowedMessage,
        ILogger logger)
    {
        if (!permissions.Holds(BeaconPermissions.UserAdmin))
        {
            throw AccessRefusal.Of(logger, actor, resource, resourceId, notAllowedMessage);
        }

        if (!permissions.IsAdmin && actor.BeaconUserId == newOwnerUserId)
        {
            throw AccessRefusal.Of(logger, actor, resource, resourceId, SelfMessage);
        }
    }
}

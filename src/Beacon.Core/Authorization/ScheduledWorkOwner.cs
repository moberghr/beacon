using Beacon.Core.Models;

namespace Beacon.Core.Authorization;

/// <summary>
/// The owner a new scheduled resource (a subscription) records, from the caller and never from a request body: its
/// <c>Users.ExternalId</c>. Scheduled work is re-authorized as its owner, so with Beacon user management on a caller
/// without a stored user (a host principal, an unbound bearer, an MCP caller without one) cannot create it. A host
/// without user management records the id its session presents.
/// </summary>
public static class ScheduledWorkOwner
{
    public const string OwnerRequiredMessage = "Scheduled work needs an owner: sign in as a Beacon user to create it.";

    public static string Require(BeaconActor actor, BeaconConfiguration configuration)
    {
        if (actor.UserId != null && (actor.BeaconUserId != null || !configuration.UserManagement.Enabled))
        {
            return actor.UserId;
        }

        throw new BeaconForbiddenException(OwnerRequiredMessage);
    }
}

using Beacon.Core.Data;
using Beacon.Core.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Services;

/// <summary>
/// The name a query change records for its requester or reviewer, who is recorded by <c>Users.ExternalId</c>
/// (<see cref="Authorization.BeaconActor.UserId"/>): the stored user's label (<see cref="UserLabel"/>: display name, or
/// "User {id}"), never the user name, e-mail or external id. Without a stored user (a host without Beacon user
/// management) the display name the caller's session presents, when the caller is that person; otherwise none.
/// </summary>
internal static class ChangeAuthorLabel
{
    public static async Task<string?> OfAsync(
        BeaconContext context,
        string? externalId,
        string? sessionDisplayName,
        CancellationToken cancellationToken)
    {
        if (externalId == null)
        {
            return null;
        }

        var label = await context.Users
            .Where(x => x.ExternalId == externalId)
            .Select(UserLabel.OfUser)
            .FirstOrDefaultAsync(cancellationToken);

        return label ?? (string.IsNullOrWhiteSpace(sessionDisplayName) ? null : sessionDisplayName);
    }
}

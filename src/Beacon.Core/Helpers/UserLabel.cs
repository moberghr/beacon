using System.Globalization;

namespace Beacon.Core.Helpers;

/// <summary>
/// How a user is named to other users (project members, member candidates, the projects sharing a data source, a
/// subscription's owner): their display name, or a neutral label by their <c>Users.Id</c> when they have none. Never
/// the user name, e-mail or external id, which can identify a person outside Beacon.
/// </summary>
internal static class UserLabel
{
    public static string Of(int userId, string? displayName)
    {
        return string.IsNullOrWhiteSpace(displayName)
            ? string.Create(CultureInfo.InvariantCulture, $"User {userId}")
            : displayName;
    }
}

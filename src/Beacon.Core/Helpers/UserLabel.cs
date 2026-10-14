using System.Globalization;
using System.Linq.Expressions;
using Beacon.Core.Data.Entities;

namespace Beacon.Core.Helpers;

/// <summary>
/// How a user is named to other users (project members, member candidates, the projects sharing a data source, the owner
/// or author of a subscription, contract, AI actor, version, dashboard or task): their display name, or a neutral label
/// by their <c>Users.Id</c> when it is missing or blank. Never the user name, e-mail or external id, which can identify a
/// person outside Beacon.
/// </summary>
public static class UserLabel
{
    /// <summary>
    /// The label as an expression EF Core translates on PostgreSQL and SQL Server, for projections that read the user in
    /// SQL: <c>users.Where(...).Select(UserLabel.OfUser).FirstOrDefault()</c>. The same rule as <see cref="Of"/>.
    /// </summary>
    public static Expression<Func<BeaconUser, string>> OfUser { get; } =
        x => string.IsNullOrWhiteSpace(x.DisplayName) ? "User " + x.Id.ToString() : x.DisplayName!;

    /// <summary>The label of a user already read (by id and display name).</summary>
    public static string Of(int userId, string? displayName)
    {
        return string.IsNullOrWhiteSpace(displayName)
            ? string.Create(CultureInfo.InvariantCulture, $"User {userId}")
            : displayName;
    }
}

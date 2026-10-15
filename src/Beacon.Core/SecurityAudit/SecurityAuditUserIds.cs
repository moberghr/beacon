using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;

namespace Beacon.Core.SecurityAudit;

/// <summary>
/// Resources record people by <c>Users.ExternalId</c> (an owner, a requester, an assignee); a security audit event names
/// them by <c>Users.Id</c> only, never by an external id. The recorder resolves the people an entry names
/// (<see cref="SecurityAuditEntry.Naming"/>) through this. Archived users are found too: an event about a resource they
/// owned still names them.
/// </summary>
public static class SecurityAuditUserIds
{
    /// <summary>The <c>Users.Id</c> of each of <paramref name="externalIds"/> that names a stored user, by external id.</summary>
    public static async Task<IReadOnlyDictionary<string, int>> OfAsync(
        BeaconContext context,
        IReadOnlyCollection<string> externalIds,
        CancellationToken cancellationToken)
    {
        if (externalIds.Count == 0)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        var users = await context.Users
            .IgnoreQueryFilters()
            .Where(x => externalIds.Contains(x.ExternalId))
            .Select(x =>
                new
                {
                    x.ExternalId,
                    x.Id
                })
            .ToListAsync(cancellationToken);

        return users
            .GroupBy(x => x.ExternalId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Min(y => y.Id), StringComparer.Ordinal);
    }

    /// <summary>The external ids <paramref name="entries"/> name, distinct.</summary>
    public static IReadOnlyList<string> NamedBy(IEnumerable<SecurityAuditEntry> entries)
    {
        return entries
            .SelectMany(x => x.NamedUsers)
            .Select(x => x.ExternalId)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}

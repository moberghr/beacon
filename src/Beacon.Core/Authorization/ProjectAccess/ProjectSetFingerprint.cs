using System.Security.Cryptography;
using System.Text;

namespace Beacon.Core.Authorization.ProjectAccess;

/// <summary>
/// A cache-key part for a value that depends on the caller's project access (§10): <c>unrestricted</c> for an
/// unrestricted caller and <c>all</c> for the open fast path, otherwise a hash of every part of the set that decides what
/// it reaches: its project ids, whether it includes the openly reachable sources, the sources that are not, whether it
/// is confined to a project list, and the owner whose queries without steps it reaches. Two callers share a cached
/// value only when their sets are the same.
/// </summary>
internal static class ProjectSetFingerprint
{
    public static string Of(ProjectSet set)
    {
        if (set.Ids == null)
        {
            return set.IsUnrestricted ? "unrestricted" : "all";
        }

        var text = string.Join(",", set.Ids.Order())
            + "|" + (set.IncludesOpen ? "open" : "closed")
            + "|" + string.Join(",", set.NotOpenDataSourceIds.Order())
            + "|" + (set.IsConfinedToList ? "list" : "any")
            + "|" + (set.QueryOwnerUserId ?? string.Empty);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

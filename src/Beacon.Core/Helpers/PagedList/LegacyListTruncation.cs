using Microsoft.Extensions.Logging;

namespace Beacon.Core.Helpers;

/// <summary>
/// Set by an unpaged list handler when its safety cap (<see cref="ListRequest.LegacyUnpagedCap"/>) cut the answer, so the
/// route can say so in a response header. One per request scope.
/// </summary>
public sealed class LegacyListTruncation
{
    public const string HeaderName = "X-Beacon-Truncated";

    public bool IsTruncated { get; private set; }

    /// <summary>Keeps the first <see cref="ListRequest.LegacyUnpagedCap"/> items of a read of cap + 1 and notes a cut.</summary>
    public static List<T> Apply<T>(List<T> items, LegacyListTruncation? truncation, ILogger? logger, string list)
    {
        if (items.Count <= ListRequest.LegacyUnpagedCap)
        {
            return items;
        }

        truncation?.Mark();
        logger?.LogWarning("The unpaged {List} list was cut at {Cap} rows; callers should use its paged route", list, ListRequest.LegacyUnpagedCap);

        return items
            .Take(ListRequest.LegacyUnpagedCap)
            .ToList();
    }

    public void Mark() => IsTruncated = true;
}

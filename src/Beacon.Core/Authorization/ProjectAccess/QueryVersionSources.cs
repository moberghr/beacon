using System.Text.Json;
using Beacon.Core.Models.Queries;

namespace Beacon.Core.Authorization.ProjectAccess;

/// <summary>
/// The data sources a query version reads: the ones recorded with it, or, for a version recorded before Beacon kept
/// them, the ones its <c>StepsJson</c> snapshot names (parsed per row; approvals and the version routes only).
/// </summary>
internal static class QueryVersionSources
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>The version's data sources; null when a legacy snapshot cannot be read (only an Admin reaches it then).</summary>
    public static IReadOnlyList<int>? Of(int[]? recorded, string? stepsJson)
    {
        if (recorded != null)
        {
            return recorded;
        }

        if (string.IsNullOrWhiteSpace(stepsJson))
        {
            return null;
        }

        try
        {
            var steps = JsonSerializer.Deserialize<List<QueryStepSnapshot>>(stepsJson, JsonOptions);

            return steps?
                .Select(x => x.DataSourceId)
                .Distinct()
                .Order()
                .ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a caller restricted to <paramref name="readable"/> reads a version whose data sources are
    /// <paramref name="sources"/> (as <see cref="Of"/> gives them), as the version's detail decides it:
    /// <paramref name="reachable"/> says whether one project of the set holds them all (or they are all openly reachable);
    /// a snapshot that cannot be read is an Admin's, and a version without data sources its query owner's.
    /// </summary>
    public static bool IsReadable(ProjectSet readable, IReadOnlyList<int>? sources, bool reachable, string? queryOwnerUserId)
    {
        if (sources == null)
        {
            return false;
        }

        return sources.Count == 0 ? readable.ReachesStepLessQueryOf(queryOwnerUserId) : reachable;
    }
}

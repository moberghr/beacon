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
}

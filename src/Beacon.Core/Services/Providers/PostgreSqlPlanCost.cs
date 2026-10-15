using System.Text.Json;

namespace Beacon.Core.Services.Providers;

/// <summary>
/// Reads the planner's estimated total cost from PostgreSQL's <c>EXPLAIN (FORMAT JSON)</c> output:
/// <c>[{"Plan": {"Node Type": "...", "Startup Cost": 0.00, "Total Cost": 35.50, ...}}]</c>. The top plan node's
/// <c>Total Cost</c> is the estimate for the whole statement.
/// </summary>
internal static class PostgreSqlPlanCost
{
    /// <summary>The top plan node's <c>Total Cost</c>; null when <paramref name="explainJson"/> does not carry one.</summary>
    public static decimal? TotalCost(string? explainJson)
    {
        if (string.IsNullOrWhiteSpace(explainJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(explainJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                return null;
            }

            var first = root[0];
            if (first.ValueKind != JsonValueKind.Object
                || !first.TryGetProperty("Plan", out var plan)
                || plan.ValueKind != JsonValueKind.Object
                || !plan.TryGetProperty("Total Cost", out var totalCost)
                || totalCost.ValueKind != JsonValueKind.Number
                || !totalCost.TryGetDecimal(out var cost))
            {
                return null;
            }

            return cost;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

using Beacon.Core.Data.Entities;

namespace Beacon.Core.Services.Providers;

/// <summary>
/// A provider that can ask its engine for the planner's estimated cost of a statement without running it. Only the
/// database provider offers it, and only for PostgreSQL (<c>EXPLAIN (FORMAT JSON)</c> without <c>ANALYZE</c>, on the
/// same single-statement read-only path a read takes). <see cref="QueryCostLimit"/> uses it to hold MCP statements to the
/// resolved <c>MaxExplainCost</c>.
/// </summary>
internal interface IReadOnlyCostEstimation
{
    /// <summary>
    /// The planner's estimated total cost of <paramref name="query"/> (a statement that already passed
    /// <see cref="Validation.ISqlExecutionGate"/>, as it will run), with the parameters the statement carries bound.
    /// </summary>
    Task<ReadOnlyCostEstimate> EstimateReadOnlyCostAsync(
        DataSource dataSource,
        string query,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken);
}

/// <summary>A cost estimate: the planner's total cost, or the error the estimate failed with (one of the two is set).</summary>
internal sealed record ReadOnlyCostEstimate(decimal? TotalCost, string? ErrorMessage)
{
    public static ReadOnlyCostEstimate Failed(string errorMessage) => new(null, errorMessage);
}

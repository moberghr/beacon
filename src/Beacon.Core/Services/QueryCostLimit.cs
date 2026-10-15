using System.Globalization;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;

namespace Beacon.Core.Services;

/// <summary>
/// Holds a statement to the resolved MCP <c>MaxExplainCost</c> before it runs. On a PostgreSQL data source, with a limit
/// set, the statement (the gate's <c>FinalSql</c>, with its parameters) is planned with <c>EXPLAIN (FORMAT JSON)</c>, never
/// <c>ANALYZE</c>, on the provider's single-statement read-only path, and refused when the planner's estimated total cost
/// exceeds the limit. Other engines have no comparable estimate: the setting does not apply there and nothing is run.
/// </summary>
public interface IQueryCostLimit
{
    /// <summary>
    /// Checks <paramref name="finalSql"/> against <paramref name="maxExplainCost"/> (null = no limit, nothing is run).
    /// Pass the caller's statement-timeout token: the estimate runs within the same timeout as the statement.
    /// </summary>
    Task<QueryCostCheck> CheckAsync(
        DataSource dataSource,
        string finalSql,
        IReadOnlyDictionary<string, object?> parameters,
        decimal? maxExplainCost,
        CancellationToken cancellationToken);
}

/// <summary>
/// The outcome of <see cref="IQueryCostLimit.CheckAsync"/>. <see cref="Refusal"/> is the message for the caller when the
/// estimate exceeds the limit. <see cref="EstimateError"/> is the database's error when the estimate itself failed: the
/// caller treats it as the statement failing with that error (it reads like one and is redacted like one).
/// </summary>
public sealed record QueryCostCheck(string? Refusal, string? EstimateError)
{
    public static QueryCostCheck Pass { get; } = new(null, null);

    public bool Passed => Refusal == null && EstimateError == null;
}

internal sealed class QueryCostLimit(IDataSourceProviderFactory providerFactory) : IQueryCostLimit
{
    public static string RefusalMessage(decimal cost, decimal limit) =>
        $"The query's estimated cost ({Format(cost)}) exceeds this project's limit ({Format(limit)}). Narrow the query, add filters, or ask an administrator to raise MaxExplainCost.";

    public async Task<QueryCostCheck> CheckAsync(
        DataSource dataSource,
        string finalSql,
        IReadOnlyDictionary<string, object?> parameters,
        decimal? maxExplainCost,
        CancellationToken cancellationToken)
    {
        if (maxExplainCost is not { } limit)
        {
            return QueryCostCheck.Pass;
        }

        if (dataSource.DataSourceType != DataSourceType.Database || dataSource.DatabaseEngineType != DatabaseEngineType.PostgreSQL)
        {
            return QueryCostCheck.Pass;
        }

        // A limit is set on a PostgreSQL source: a provider that cannot estimate fails the call rather than letting the
        // statement run unchecked.
        if (providerFactory.GetProvider(dataSource.DataSourceType) is not IReadOnlyCostEstimation estimation)
        {
            return new QueryCostCheck(null, "The query's estimated cost could not be determined, so it was not run.");
        }

        // An estimate that fails (a planner error, the statement timeout) is not a pass: the call fails as a failing
        // statement fails, with the database's error. The statement would most likely fail the same way, and a limit
        // that a failing EXPLAIN could skip would not be a limit.
        var estimate = await estimation.EstimateReadOnlyCostAsync(dataSource, finalSql, new Dictionary<string, object?>(parameters), cancellationToken);
        if (estimate.TotalCost is not { } cost)
        {
            return new QueryCostCheck(null, estimate.ErrorMessage ?? "The query's estimated cost could not be determined, so it was not run.");
        }

        return cost > limit
            ? new QueryCostCheck(RefusalMessage(cost, limit), null)
            : QueryCostCheck.Pass;
    }

    private static string Format(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

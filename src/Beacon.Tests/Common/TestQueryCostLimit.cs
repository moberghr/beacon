using Beacon.Core.Data.Entities;
using Beacon.Core.Services;

namespace Beacon.Tests.Common;

/// <summary>Fixed answers for the MCP MaxExplainCost check, for tests that do not exercise it.</summary>
internal static class TestQueryCostLimit
{
    /// <summary>Every statement passes, and nothing is estimated.</summary>
    public static IQueryCostLimit Passing { get; } = new Fixed(QueryCostCheck.Pass);

    private sealed class Fixed(QueryCostCheck check) : IQueryCostLimit
    {
        public Task<QueryCostCheck> CheckAsync(
            DataSource dataSource,
            string finalSql,
            IReadOnlyDictionary<string, object?> parameters,
            decimal? maxExplainCost,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(check);
        }
    }
}

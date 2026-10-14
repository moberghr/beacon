using Beacon.Core.Data;
using Beacon.Core.Services.Validation;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Services;

/// <summary>
/// The SQL dialect of each data source a set of grounding rows belongs to — what a prompt renders their SQL in
/// (<see cref="GroundingSqlText"/>) — so a list can say which rows' SQL a prompt leaves out, and a renderer can show it.
/// </summary>
public static class GroundingDialects
{
    /// <summary>
    /// The dialect of each of <paramref name="dataSourceIds"/>: one database command, none without ids. A missing data
    /// source maps to null (no dialect: SQL holding a comment marker is left out).
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, string?>> OfAsync(
        BeaconContext context,
        IReadOnlyCollection<int> dataSourceIds,
        CancellationToken cancellationToken)
    {
        if (dataSourceIds.Count == 0)
        {
            return new Dictionary<int, string?>();
        }

        var sources = await context.DataSources
            .Where(x => dataSourceIds.Contains(x.Id))
            .Select(x =>
                new
                {
                    x.Id,
                    x.DataSourceType,
                    x.DatabaseEngineType
                })
            .ToListAsync(cancellationToken);

        var dialects = sources.ToDictionary(x => x.Id, x => DataSourceSqlDialect.Of(x.DataSourceType, x.DatabaseEngineType));

        return dataSourceIds
            .Distinct()
            .ToDictionary(x => x, x => dialects.GetValueOrDefault(x));
    }

    /// <summary>
    /// Whether rendering <paramref name="sql"/> depends on its dialect: it holds a comment marker of some dialect
    /// (<c>/*</c>, <c>--</c>, <c>#</c>, <c>//</c>). SQL without one renders alike in every dialect, so its data source need
    /// not be read.
    /// </summary>
    public static bool NeedsDialect(string? sql)
    {
        return !string.IsNullOrEmpty(sql)
            && (sql.Contains("/*", StringComparison.Ordinal)
                || sql.Contains("--", StringComparison.Ordinal)
                || sql.Contains('#')
                || sql.Contains("//", StringComparison.Ordinal));
    }

    /// <summary>Whether a prompt leaves <paramref name="sql"/> out: it cannot be shown without its comments in the dialect.</summary>
    public static bool Withholds(string? sql, string? dialect)
    {
        return !string.IsNullOrWhiteSpace(sql) && GroundingSqlText.WithoutComments(sql, dialect) == null;
    }
}

using System.Data.Common;
using Beacon.Core.Helpers;
using Beacon.Core.Services.Validation;
using Dapper;

namespace Beacon.Core.Services;

/// <summary>One page of a raw SQL result, with the total row count of the whole result.</summary>
/// <param name="SortApplied">False when the requested sort could not be applied to this SQL shape.</param>
public sealed record SqlResultPage(List<IDictionary<string, object?>> Rows, int TotalCount, bool SortApplied);

/// <summary>
/// Fetches one page of a user's query and counts its rows, keeping only that page in memory. Runs the
/// <see cref="SqlPageRewriter"/> plan when the SQL shape allows it; otherwise runs the original statement
/// unchanged and streams it — rows before the page are skipped, rows after it only counted.
/// </summary>
internal static class SqlPageExecutor
{
    /// <param name="isAllowed">Re-checks each rewritten statement (read-only gate) before it runs; a rejected
    /// rewrite falls back to the original, already-approved SQL.</param>
    public static async Task<SqlResultPage> ExecuteAsync(
        DbConnection connection,
        string sql,
        object? parameters,
        string? dialect,
        ListRequest paging,
        Func<string, bool> isAllowed,
        int? timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var limit = paging.PageSizeOrDefault;
        var offset = paging.PageOrDefault * limit;
        var plan = SqlPageRewriter.Plan(sql, dialect, offset, limit, paging.SortCriteria().FirstOrDefault());
        var pageSql = plan.PageSql != null && isAllowed(plan.PageSql) ? plan.PageSql : null;
        var countSql = plan.CountSql != null && isAllowed(plan.CountSql) ? plan.CountSql : null;

        if (pageSql == null)
        {
            var (streamed, total) = await StreamAsync(connection, sql, parameters, offset, limit, timeoutSeconds, cancellationToken);
            return new SqlResultPage(streamed, total, false);
        }

        var rows = (await connection.QueryAsync(Command(pageSql, parameters, timeoutSeconds, cancellationToken)))
            .Select(x => (IDictionary<string, object?>)x)
            .ToList();

        var totalCount = countSql != null
            ? await connection.ExecuteScalarAsync<long>(Command(countSql, parameters, timeoutSeconds, cancellationToken))
            : (await StreamAsync(connection, sql, parameters, 0, 0, timeoutSeconds, cancellationToken)).Total;

        return new SqlResultPage(rows, (int)Math.Min(totalCount, int.MaxValue), plan.SortApplied);
    }

    /// <summary>Reads the whole result once, keeping rows [offset, offset + limit) and counting all of them.</summary>
    private static async Task<(List<IDictionary<string, object?>> Rows, int Total)> StreamAsync(
        DbConnection connection,
        string sql,
        object? parameters,
        int offset,
        int limit,
        int? timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var rows = new List<IDictionary<string, object?>>();
        long index = 0;

        await using var reader = await connection.ExecuteReaderAsync(Command(sql, parameters, timeoutSeconds, cancellationToken));
        while (await reader.ReadAsync(cancellationToken))
        {
            if (index >= offset && rows.Count < limit)
            {
                rows.Add(ReadRow(reader));
            }

            index++;
        }

        return (rows, (int)Math.Min(index, int.MaxValue));
    }

    private static Dictionary<string, object?> ReadRow(DbDataReader reader)
    {
        var row = new Dictionary<string, object?>(reader.FieldCount);
        for (var column = 0; column < reader.FieldCount; column++)
        {
            row[reader.GetName(column)] = reader.IsDBNull(column) ? null : reader.GetValue(column);
        }

        return row;
    }

    private static CommandDefinition Command(string sql, object? parameters, int? timeoutSeconds, CancellationToken cancellationToken) =>
        new(commandText: sql, parameters: parameters, commandTimeout: timeoutSeconds, cancellationToken: cancellationToken);
}

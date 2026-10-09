using System.Text.RegularExpressions;
using Beacon.Core.Helpers;
using SqlParser.Ast;

namespace Beacon.Core.Services.Validation;

/// <summary>
/// How to fetch one page of a query's result, and how to count it, without loading the whole result.
/// A null <see cref="PageSql"/> or <see cref="CountSql"/> means that part cannot be rewritten safely for
/// this SQL shape: the caller runs the original statement and streams it (skip to the page, count the
/// rest) instead.
/// </summary>
/// <param name="SortApplied">Whether <see cref="PageSql"/> orders by the requested column.</param>
public sealed record SqlPagePlan(string? PageSql, string? CountSql, bool SortApplied);

/// <summary>
/// Builds paging, sorting and counting SQL around a user's query, per dialect. Like
/// <see cref="SqlRowLimitRewriter"/>, the decisions are made on the parsed AST but the output is the
/// user's own text plus added clauses — never a <c>ToSql()</c> round-trip.
/// <list type="bullet">
/// <item>Unbounded and unordered (or ordered with no sort requested): append the paging clause
/// (<c>LIMIT/OFFSET</c>, or T-SQL <c>ORDER BY … OFFSET/FETCH</c>) — valid for CTEs and set operations too.</item>
/// <item>Otherwise wrap it as a derived table and page the outer query. T-SQL forbids a CTE inside a
/// derived table and an ORDER BY there without TOP/OFFSET, so those shapes fall back.</item>
/// <item>Count: <c>SELECT COUNT(*) FROM (…)</c>, under the same derived-table rules.</item>
/// </list>
/// SQL containing comments is never rewritten: execution flattens newlines, so a line comment (<c>--</c>, and
/// <c>#</c> on MySQL and BigQuery or <c>//</c> on Snowflake) would swallow the added clauses.
/// </summary>
public static class SqlPageRewriter
{
    private const int MaxSortColumnLength = 128;

    private static readonly Regex LeadingSelectPattern = new(
        @"\A\s*SELECT(?:\s+DISTINCT)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly SqlPagePlan Unsupported = new(null, null, false);

    /// <param name="sql">The user's statement, already bound and flattened the way it will execute.</param>
    /// <param name="dialect">Database engine name (PostgreSQL, MSSQL, AzureSynapse, MySQL, SQLite, …).</param>
    /// <param name="sort">Result column to order by; null keeps the query's own order.</param>
    public static SqlPagePlan Plan(string sql, string? dialect, int offset, int limit, SortCriterion? sort)
    {
        if (string.IsNullOrWhiteSpace(sql) || HasComment(sql, dialect) || limit <= 0 || offset < 0)
        {
            return Unsupported;
        }

        if (sort != null && !IsSafeColumnName(sort.SortColumn))
        {
            sort = null;
        }

        Query? query;
        try
        {
            var statements = SqlAst.Parse(sql, dialect);
            query = statements.Count == 1 && statements[0] is Statement.Select select ? select.Query : null;
        }
        catch (Exception)
        {
            return Unsupported;
        }

        if (query == null)
        {
            return Unsupported;
        }

        var isTSql = SqlDialects.IsTSql(dialect);
        var trimmed = sql.Trim().TrimEnd(';').TrimEnd();
        var bounded = query.Limit != null
            || query.Offset != null
            || query.Fetch != null
            || query.Body is SetExpression.SelectExpression { Select.Top: not null };
        var ordered = query.OrderBy != null;
        var paging = isTSql
            ? $"OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY"
            : $"LIMIT {limit} OFFSET {offset}";
        var orderBy = sort != null
            ? $"ORDER BY {Quote(sort.SortColumn, dialect)} {(sort.SortDirection == SortDirection.Descending ? "DESC" : "ASC")}"
            : isTSql ? "ORDER BY (SELECT NULL)" : null;
        var derived = DerivedTableBody(trimmed, query, isTSql, bounded, ordered);

        // T-SQL rejects ORDER BY (SELECT NULL) after DISTINCT or a set operation (ORDER BY items must be in
        // the select list), so with no column to order by those shapes page through a derived table.
        var needsDerivedOrder = isTSql
            && sort == null
            && !ordered
            && (query.Body is SetExpression.SetOperation || query.Body is SetExpression.SelectExpression { Select.Distinct: not null });

        string? pageSql;
        if (!bounded && (!ordered || sort == null) && !needsDerivedOrder)
        {
            // Append to the outermost query. Its own ORDER BY stays when no sort is requested.
            pageSql = ordered ? $"{trimmed} {paging}" : Join(trimmed, orderBy, paging);
        }
        else
        {
            pageSql = derived == null ? null : Join($"SELECT * FROM ({derived}) AS beacon_page", orderBy, paging);
        }

        var countSql = derived == null ? null : $"SELECT COUNT(*) FROM ({derived}) AS beacon_count";

        return new SqlPagePlan(pageSql, countSql, pageSql != null && sort != null);
    }

    // Quote-unaware on purpose: a comment marker inside a literal only sends the statement to the streaming fallback.
    private static bool HasComment(string sql, string? dialect)
    {
        return sql.Contains("/*") || SqlDialects.HasLineCommentMarker(sql, dialect);
    }

    /// <summary>The query as a derived-table body, or null when the dialect cannot nest it.</summary>
    private static string? DerivedTableBody(string trimmed, Query query, bool isTSql, bool bounded, bool ordered)
    {
        if (!isTSql)
        {
            return trimmed;
        }

        if (query.With != null)
        {
            return null;
        }

        if (!ordered || bounded)
        {
            return trimmed;
        }

        // T-SQL: ORDER BY in a derived table needs TOP (or OFFSET). TOP 100 PERCENT keeps every row; the
        // outer query decides the order anyway. Only a plain SELECT has a leading SELECT to put it on.
        if (query.Body is not SetExpression.SelectExpression)
        {
            return null;
        }

        var match = LeadingSelectPattern.Match(trimmed);
        if (!match.Success)
        {
            return null;
        }

        var insertAt = match.Index + match.Length;

        return $"{trimmed[..insertAt]} TOP 100 PERCENT{trimmed[insertAt..]}";
    }

    /// <summary>Dialect-quoted identifier with the quote character escaped, so a column name can never close it.</summary>
    internal static string Quote(string column, string? dialect)
    {
        if (SqlDialects.IsTSql(dialect))
        {
            return $"[{column.Replace("]", "]]")}]";
        }

        var engine = (dialect ?? string.Empty).ToLowerInvariant();
        if (engine is "mysql" or "mariadb")
        {
            return $"`{column.Replace("`", "``")}`";
        }

        return $"\"{column.Replace("\"", "\"\"")}\"";
    }

    private static bool IsSafeColumnName(string column) =>
        !string.IsNullOrWhiteSpace(column)
        && column.Length <= MaxSortColumnLength
        && column.All(x => !char.IsControl(x));

    private static string Join(params string?[] parts) =>
        string.Join(' ', parts.Where(x => !string.IsNullOrEmpty(x)));
}

using System.Diagnostics;
using Beacon.Core.Helpers;
using Beacon.Core.Services.Retention;

namespace Beacon.Core.Mcp;

/// <summary>
/// The statements an MCP tool call ran on data sources, in the order they ran, for its audit row: the executors that reach
/// a data source (the query execution service, the cross-source service, value-grounding probes) record each statement,
/// and the pipeline that runs them names why (<see cref="BeginPurpose"/>: a repair, a self-consistency candidate, a dry
/// run). <c>McpAuditService</c> takes them when it writes the call's audit row, on the success and the failure path alike,
/// whatever the learning settings. Scoped (one MCP call per request scope); registered by <c>AddBeaconServices</c>.
/// </summary>
public interface IMcpStatementLog
{
    /// <summary>The purpose statements are recorded with until the returned scope is disposed (scopes nest).</summary>
    IDisposable BeginPurpose(string purpose);

    /// <summary>The purpose of the innermost open scope; null outside any.</summary>
    string? CurrentPurpose { get; }

    /// <summary>Records one statement that reached a data source.</summary>
    void Record(McpStatement statement);

    /// <summary>
    /// The statements recorded so far, which are no longer kept: an audit row takes each statement once. Past the log's
    /// bound, statements are counted instead of kept (<see cref="McpStatementsTaken.Dropped"/>), so an audit row can show
    /// that it holds only part of what the call ran.
    /// </summary>
    McpStatementsTaken TakeAll();
}

/// <summary>The statements an audit row takes from the log, in the order they ran, and how many more ran but were not kept.</summary>
public sealed class McpStatementsTaken(IReadOnlyList<McpStatement> statements, int dropped) : IReadOnlyList<McpStatement>
{
    /// <summary>Statements that ran after the log's bound was reached: counted, not kept.</summary>
    public int Dropped { get; } = dropped;

    public int Count => statements.Count;

    public McpStatement this[int index] => statements[index];

    public IEnumerator<McpStatement> GetEnumerator() => statements.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// One statement that reached a data source: where, why, its text (content: an audit keeps it only while the project
/// retains query content; its keyed hash is kept always), the tables it names, what it returned, how long it took and the
/// class of the error it ended with.
/// </summary>
public sealed record McpStatement(
    int? DataSourceId,
    string Purpose,
    string Sql,
    IReadOnlyList<string> Tables,
    int? RowCount,
    int DurationMs,
    string? ErrorClass);

/// <summary>Why a statement ran.</summary>
public static class McpStatementPurposes
{
    /// <summary>A statement a tool ran as asked (the <c>query</c> tool), or one run outside any named purpose.</summary>
    public const string Query = "query";

    /// <summary>The first execution of the SQL an <c>ask</c> generated.</summary>
    public const string Initial = "initial";

    /// <summary>An execution of a repaired statement (after an execution error or an empty result).</summary>
    public const string Repair = "repair";

    /// <summary>A self-consistency candidate executed to vote on its result.</summary>
    public const string Candidate = "candidate";

    /// <summary>A validation of a statement on the data source without running it (EXPLAIN and the like).</summary>
    public const string DryRun = "dry_run";

    /// <summary>A value-grounding probe for a literal of the question.</summary>
    public const string Probe = "probe";

    /// <summary>One data source's statement of a cross-source answer.</summary>
    public const string SourceStep = "source_step";

    /// <summary>
    /// Not a statement: the marker row an audit adds when the call ran more statements than its log keeps, its row count the
    /// number of statements not kept.
    /// </summary>
    public const string Truncated = "truncated";
}

/// <summary>What a statement's table list holds when it is not the plain list of the tables the statement names.</summary>
public static class McpStatementTables
{
    /// <summary>The statement's tables could not be read from its text: the list is unknown, not empty.</summary>
    public const string Unparsed = "[unparsed]";

    /// <summary>The last entry of a list that did not fit: how many more tables the statement names.</summary>
    public static string More(int count) => $"[+{count} more]";

    /// <summary>Whether <paramref name="entry"/> is one of these markers rather than a table name.</summary>
    public static bool IsMarker(string entry) => entry.StartsWith('[');
}

/// <summary>Records statements around the call that runs them, timing it and classifying its error.</summary>
public static class McpStatementLogExtensions
{
    /// <summary>
    /// Runs <paramref name="run"/> (the call that sends <paramref name="sql"/> to data source
    /// <paramref name="dataSourceId"/>) and records it under <paramref name="purpose"/> (null: the current purpose, else
    /// <see cref="McpStatementPurposes.Query"/>): with the row count and error <paramref name="outcome"/> reads from its
    /// result, or, when it throws, with the class of what it threw (rethrown).
    /// </summary>
    public static async Task<T> RecordAsync<T>(
        this IMcpStatementLog log,
        int? dataSourceId,
        string? purpose,
        string sql,
        Func<Task<T>> run,
        Func<T, (int? RowCount, string? Error)> outcome)
    {
        var effectivePurpose = purpose ?? log.CurrentPurpose ?? McpStatementPurposes.Query;
        var stopwatch = Stopwatch.StartNew();
        T result;
        try
        {
            result = await run();
        }
        catch (Exception ex)
        {
            log.Record(Statement(dataSourceId, effectivePurpose, sql, null, stopwatch, ErrorClassOf(ex)));
            throw;
        }

        var (rowCount, error) = outcome(result);
        log.Record(Statement(dataSourceId, effectivePurpose, sql, rowCount, stopwatch, string.IsNullOrEmpty(error) ? null : McpContentRedactor.ErrorClassOf(error)));

        return result;
    }

    private static McpStatement Statement(int? dataSourceId, string purpose, string sql, int? rowCount, Stopwatch stopwatch, string? errorClass)
    {
        return new McpStatement(
            dataSourceId,
            purpose,
            sql,
            TablesOf(sql),
            rowCount,
            (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue),
            string.IsNullOrEmpty(errorClass) ? null : errorClass);
    }

    // The class only, never the message: a provider's error can quote row values.
    private static string ErrorClassOf(Exception exception)
    {
        return exception is OperationCanceledException
            ? McpContentRedactor.ClassCancelled
            : McpContentRedactor.ErrorClassOf(exception.Message) ?? McpContentRedactor.ClassUnknown;
    }

    private static IReadOnlyList<string> TablesOf(string sql)
    {
        try
        {
            return SqlTableNameExtractor.ExtractTableNames(sql);
        }
        catch (Exception)
        {
            // The table list is evidence on top of the statement, never a reason to lose it; that it is unknown is kept.
            return [McpStatementTables.Unparsed];
        }
    }
}

/// <summary>
/// The scoped log <see cref="IMcpStatementLog"/> describes. It keeps at most <see cref="MaxStatements"/> statements per
/// scope (a long-lived scope that runs many asks, such as an evaluation run, never grows without bound) and counts the
/// rest in <see cref="Dropped"/>.
/// </summary>
internal sealed class McpStatementLog : IMcpStatementLog
{
    internal const int MaxStatements = 100;

    private readonly AsyncLocal<string?> _purpose = new();
    private readonly List<McpStatement> _statements = [];

    /// <summary>The statements not kept since the last <see cref="TakeAll"/>, past <see cref="MaxStatements"/>.</summary>
    internal int Dropped { get; private set; }

    public string? CurrentPurpose => _purpose.Value;

    public IDisposable BeginPurpose(string purpose)
    {
        var previous = _purpose.Value;
        _purpose.Value = purpose;

        return new PurposeScope(this, previous);
    }

    public void Record(McpStatement statement)
    {
        lock (_statements)
        {
            if (_statements.Count >= MaxStatements)
            {
                Dropped++;

                return;
            }

            _statements.Add(statement);
        }
    }

    public McpStatementsTaken TakeAll()
    {
        lock (_statements)
        {
            var taken = new McpStatementsTaken(_statements.ToList(), Dropped);
            _statements.Clear();
            Dropped = 0;

            return taken;
        }
    }

    private sealed class PurposeScope(McpStatementLog log, string? previous) : IDisposable
    {
        public void Dispose()
        {
            log._purpose.Value = previous;
        }
    }
}

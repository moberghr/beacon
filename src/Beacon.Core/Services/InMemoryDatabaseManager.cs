using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SQLitePCL;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Services;

public class InMemoryDatabaseManager : IDisposable
{
    internal const string NestedBlockCommentRejection = "Nested block comments are not supported in in-memory join queries.";

    private const string TrailingStatementRejection = "Only a single SQL statement can run against the in-memory join store.";

    // The longest string, blob or row SQLite will build on this connection (16 MiB): randomblob(9e8)-style
    // allocations fail with SQLITE_TOOBIG instead of taking near a gigabyte per call.
    private const int MaxValueLength = 16 * 1024 * 1024;

    private static readonly Regex ResultReferencePattern = new(@"@result(\d+)", RegexOptions.IgnoreCase);

    // Table loading at the engine, from construction until the first query. Loading asks exactly this (verified):
    // BEGIN/COMMIT/ROLLBACK ask TRANSACTION; CREATE TABLE asks CREATE_TABLE and INSERT on sqlite_master, then UPDATEs
    // sqlite_master's columns and reads its ROWID; each row asks INSERT; the pre-query analysis selects, reads and
    // counts. Everything else — PRAGMA (some act while merely compiling), ATTACH/DETACH, DROP, DELETE, ALTER, an
    // UPDATE of a loaded table, views, triggers, indexes, temp objects, savepoints — fails with "not authorized", so
    // a load statement can never do more than load, whatever result-column names it was built from.
    private static readonly delegate_authorizer LoadAuthorizer = (_, actionCode, table, _, _, _) =>
        actionCode switch
        {
            raw.SQLITE_CREATE_TABLE or raw.SQLITE_INSERT or raw.SQLITE_TRANSACTION
                or raw.SQLITE_SELECT or raw.SQLITE_READ or raw.SQLITE_FUNCTION => raw.SQLITE_OK,
            raw.SQLITE_UPDATE when IsSchemaTable(table) => raw.SQLITE_OK,
            _ => raw.SQLITE_DENY
        };

    // Read-only at the engine (§1.5): SQLite asks this at prepare time for every action of every statement it
    // compiles on the connection — a trailing statement the AST gate never saw included — and only reads pass.
    // Everything else (PRAGMA, ATTACH/DETACH, writes, transactions, schema changes) fails with "not authorized".
    private static readonly delegate_authorizer ReadOnlyAuthorizer = (_, actionCode, _, _, _, _) =>
        actionCode is raw.SQLITE_SELECT or raw.SQLITE_READ or raw.SQLITE_FUNCTION or raw.SQLITE_RECURSIVE
            ? raw.SQLITE_OK
            : raw.SQLITE_DENY;

    private readonly SqliteConnection _connection;
    private readonly ILogger<InMemoryDatabaseManager> _logger;
    private readonly Dictionary<string, ProjectInfo> _tableProjectInfo = new();
    private bool _readOnly;
    private bool _disposed;

    public InMemoryDatabaseManager(ILogger<InMemoryDatabaseManager> logger)
    {
        _logger = logger;
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        // The join store only ever reads its own in-memory database. Zero attachable databases makes SQLite refuse
        // every ATTACH (which creates files on the host) at the engine, under the read-only AST gate (§1.5).
        raw.sqlite3_limit(_connection.Handle, raw.SQLITE_LIMIT_ATTACHED, 0);
        raw.sqlite3_limit(_connection.Handle, raw.SQLITE_LIMIT_LENGTH, MaxValueLength);

        // Engaged before any statement compiles: table loads build their SQL from caller-controlled result-column
        // names, so even loading runs under an authorizer — the read-only one replaces it when the first query runs.
        if (raw.sqlite3_set_authorizer(_connection.Handle, LoadAuthorizer, null) != raw.SQLITE_OK)
        {
            _connection.Dispose();
            throw new InvalidOperationException("The in-memory join store could not be restricted to table loading.");
        }

        _logger.LogDebug("Created in-memory SQLite database connection");
    }

    /// <summary>
    /// The connection's native handle. Internal for unit tests (InternalsVisibleTo Beacon.Tests), which probe the
    /// engine-level guards on it directly.
    /// </summary>
    internal sqlite3 Handle => _connection.Handle!;

    public async Task CreateTableFromResults(string tableName, List<IDictionary<string, object?>> data, ProjectInfo sourceProject)
    {
        if (_readOnly)
        {
            throw new InvalidOperationException("Tables cannot be loaded after a query has run: the in-memory join store is read-only from then on.");
        }

        if (!data.Any())
        {
            _logger.LogDebug("Skipping table creation for {TableName} - no data provided", tableName);
            return;
        }

        // Column names are the aliases of step, source or LLM-written SQL — caller-controlled — so every name is a
        // quoted identifier and every value a positional parameter: no part of a name is ever read as SQL.
        var firstRow = data.First();
        var columnNames = firstRow.Keys.ToList();
        var columnDefinitions = firstRow
            .Select(x => $"{QuoteIdentifier(x.Key)} {InferSqliteType(x.Value)}")
            .ToList();
        var quotedTableName = QuoteIdentifier(tableName);

        _logger.LogDebug("Creating table {TableName} with {ColumnCount} columns from {SourceProject} ({DatabaseEngine})",
            tableName, columnDefinitions.Count, sourceProject.Name, sourceProject.DatabaseEngine);

        // One transaction for the table and its rows: a load that fails leaves nothing behind.
        using var transaction = _connection.BeginTransaction();

        await ExecuteLoadStatementAsync($"CREATE TABLE {quotedTableName} ({string.Join(", ", columnDefinitions)})", transaction);
        await BulkInsertData(quotedTableName, columnNames, data, transaction);

        await transaction.CommitAsync();

        _tableProjectInfo[tableName] = sourceProject;

        _logger.LogInformation("Created table {TableName} with {RowCount} rows from {SourceProject}",
            tableName, data.Count, sourceProject.Name);
    }

    private async Task BulkInsertData(
        string quotedTableName,
        List<string> columnNames,
        List<IDictionary<string, object?>> data,
        SqliteTransaction transaction)
    {
        var quotedColumns = string.Join(", ", columnNames.Select(QuoteIdentifier));
        var placeholders = string.Join(", ", Enumerable.Range(0, columnNames.Count).Select(PositionalParameter));

        // Prepared once for the table, rebound per row.
        await using var insert = CreateLoadCommand($"INSERT INTO {quotedTableName} ({quotedColumns}) VALUES ({placeholders})", transaction);
        for (var index = 0; index < columnNames.Count; index++)
        {
            insert.Parameters.Add(new SqliteParameter(PositionalParameter(index), DBNull.Value));
        }

        foreach (var row in data)
        {
            for (var index = 0; index < columnNames.Count; index++)
            {
                insert.Parameters[index].Value = row.TryGetValue(columnNames[index], out var value)
                    ? ConvertValueForSqlite(value) ?? DBNull.Value
                    : DBNull.Value;
            }

            await insert.ExecuteNonQueryAsync();
        }
    }

    private string InferSqliteType(object? value)
    {
        return value switch
        {
            null => "TEXT",
            string => "TEXT",
            int => "INTEGER",
            long => "INTEGER",
            float => "REAL",
            double => "REAL",
            decimal => "REAL",
            bool => "INTEGER",
            DateTime => "TEXT",
            DateTimeOffset => "TEXT",
            Guid => "TEXT",
            byte[] => "BLOB",
            _ => "TEXT"
        };
    }

    private object? ConvertValueForSqlite(object? value)
    {
        return value switch
        {
            null => null,
            bool b => b ? 1 : 0,
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"),
            Guid g => g.ToString(),
            _ => value
        };
    }

    public async Task<(List<IDictionary<string, object?>> Results, double ExecutionTimeMs, bool TimedOut)> ExecuteQueryAsync(
        string sql,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        RejectNestedBlockComment(sql);
        EnterReadOnlyModeAndRequireSingleStatement(sql);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        using var timeoutCts = timeoutSeconds.HasValue
            ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds.Value))
            : new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

        // Microsoft.Data.Sqlite checks the token only before a statement starts and ignores CommandTimeout for a
        // running one, so a fired token interrupts the statement on the connection instead.
        using var interruptRegistration = linkedCts.Token.Register(() => raw.sqlite3_interrupt(_connection.Handle));

        try
        {
            var commandDefinition = new CommandDefinition(
                commandText: sql,
                commandTimeout: timeoutSeconds,
                cancellationToken: linkedCts.Token
            );

            var dapperRows = await _connection.QueryAsync(commandDefinition);

            stopwatch.Stop();
            var executionTimeMs = stopwatch.Elapsed.TotalMilliseconds;

            var results = dapperRows.Select(x => (IDictionary<string, object?>)x).ToList();

            _logger.LogDebug("Executed SQLite query in {ExecutionTimeMs}ms, returned {ResultCount} rows",
                executionTimeMs, results.Count);

            return (results, executionTimeMs, false);
        }
        catch (Exception ex) when (IsCancellation(ex) && linkedCts.IsCancellationRequested)
        {
            stopwatch.Stop();
            cancellationToken.ThrowIfCancellationRequested();

            var executionTimeMs = stopwatch.Elapsed.TotalMilliseconds;

            _logger.LogWarning("SQLite query timed out after {ExecutionTimeMs}ms", executionTimeMs);
            return (new List<IDictionary<string, object?>>(), executionTimeMs, true);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Error executing SQLite query");
            throw;
        }
    }

    /// <summary>One page of <paramref name="sql"/> and its total row count, via the SQLite paging plan.</summary>
    /// <param name="validate">The read-only gate: null when a statement may run, the rejection reason otherwise.
    /// <paramref name="sql"/> itself must pass — a rejection throws before anything runs, because the page executor
    /// falls back to streaming the original when it rejects a rewrite. The page and count rewrites pass it too,
    /// and must also be a single statement to SQLite.</param>
    public async Task<SqlResultPage> ExecutePagedAsync(
        string sql,
        Helpers.ListRequest paging,
        Func<string, string?> validate,
        CancellationToken cancellationToken)
    {
        // Before the gate parses it: its parser and SQLite disagree on where a nested block comment ends.
        RejectNestedBlockComment(sql);

        var rejection = validate(sql);
        if (rejection != null)
        {
            throw new InvalidOperationException(rejection);
        }

        EnterReadOnlyModeAndRequireSingleStatement(sql);

        return await SqlPageExecutor.ExecuteAsync(
            _connection,
            sql,
            null,
            nameof(DatabaseEngineType.SQLite),
            paging,
            x => validate(x) == null && !HasTrailingStatement(x),
            null,
            cancellationToken);
    }

    /// <summary>
    /// True when a <c>/*</c> opens inside a block comment that is still open. SQLite ends a block comment at its
    /// first <c>*/</c> while the AST gate's parser nests them, so in such text the two disagree on where the comment
    /// ends and the gate can approve a statement hiding a second one. Lexes the way SQLite does: quoted strings and
    /// identifiers (<c>'…'</c>, <c>"…"</c>, <c>`…`</c>, <c>[…]</c>) and <c>--</c> line comments are skipped, and a
    /// <c>/*</c> sharing its <c>*</c> with the closing <c>*/</c> (<c>/* /*/</c>) counts as nested, because the parser
    /// reads it that way. Static, so a final query can be checked before it is saved, with no tables loaded.
    /// </summary>
    internal static bool HasNestedBlockComment(string sql)
    {
        var position = 0;
        while (position < sql.Length - 1)
        {
            var pair = sql.AsSpan(position, 2);
            if (pair is "--")
            {
                var lineEnd = sql.IndexOf('\n', position + 2);
                if (lineEnd < 0)
                {
                    return false;
                }

                position = lineEnd + 1;
                continue;
            }

            if (pair is "/*")
            {
                var bodyStart = position + 2;
                var close = sql.IndexOf("*/", bodyStart, StringComparison.Ordinal);
                var scanEnd = close < 0 ? sql.Length : close + 1;
                if (sql.IndexOf("/*", bodyStart, scanEnd - bodyStart, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }

                if (close < 0)
                {
                    return false;
                }

                position = close + 2;
                continue;
            }

            var closingQuote = sql[position] switch
            {
                '\'' => '\'',
                '"' => '"',
                '`' => '`',
                '[' => ']',
                _ => '\0'
            };
            if (closingQuote != '\0')
            {
                var quoteEnd = sql.IndexOf(closingQuote, position + 1);
                if (quoteEnd < 0)
                {
                    return false;
                }

                position = quoteEnd + 1;
                continue;
            }

            position++;
        }

        return false;
    }

    /// <summary>
    /// The SQL a final query runs as: each <c>@resultN</c> step reference becomes the in-memory table <c>[resultN]</c>.
    /// Static so a final query can be gated without opening a database.
    /// </summary>
    public static string TranslateResultReferences(string finalQuery) =>
        ResultReferencePattern.Replace(finalQuery, x => $"[result{x.Groups[1].Value}]");

    public string TranslateFinalQuery(string originalQuery) => TranslateResultReferences(originalQuery);

    public InMemoryDatabaseAnalysis AnalyzeDatabase()
    {
        var analysis = new InMemoryDatabaseAnalysis();

        // Get table information
        var tableInfoSql = "SELECT name FROM sqlite_master WHERE type='table'";
        var tables = _connection.Query<string>(tableInfoSql).ToList();

        foreach (var tableName in tables)
        {
            var rowCountSql = $"SELECT COUNT(*) FROM {QuoteIdentifier(tableName)}";
            var rowCount = _connection.QuerySingle<int>(rowCountSql);

            // A plain SELECT, not PRAGMA table_info: both authorizers refuse every PRAGMA, and this analysis runs
            // while tables load as well as after a query.
            using var columnReader = _connection.ExecuteReader($"SELECT * FROM {QuoteIdentifier(tableName)} LIMIT 0");
            var columnCount = columnReader.FieldCount;

            var projectInfo = _tableProjectInfo.GetValueOrDefault(tableName);

            analysis.Tables[tableName] = new InMemoryTableInfo
            {
                TableName = tableName,
                RowCount = rowCount,
                ColumnCount = columnCount,
                SourceProject = projectInfo?.Name ?? "Unknown",
                SourceDatabaseEngine = projectInfo?.DatabaseEngine ?? "Unknown"
            };
        }

        analysis.TotalTables = tables.Count;
        analysis.TotalRows = analysis.Tables.Values.Sum(t => t.RowCount);

        return analysis;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _connection?.Dispose();
            _disposed = true;
            _logger.LogDebug("Disposed in-memory SQLite database connection");
        }
        GC.SuppressFinalize(this);
    }

    private static void RejectNestedBlockComment(string sql)
    {
        if (HasNestedBlockComment(sql))
        {
            throw new InvalidOperationException(NestedBlockCommentRejection);
        }
    }

    /// <summary>
    /// Locks the store read-only (once; table loads are refused from then on), then requires <paramref name="sql"/>
    /// to be one statement to SQLite. The read-only authorizer replaces the load one FIRST: SQLite applies some
    /// PRAGMAs (<c>hard_heap_limit</c>, …) while merely compiling them, and the single-statement check compiles.
    /// </summary>
    private void EnterReadOnlyModeAndRequireSingleStatement(string sql)
    {
        if (!_readOnly)
        {
            var rc = raw.sqlite3_set_authorizer(_connection.Handle, ReadOnlyAuthorizer, null);
            if (rc != raw.SQLITE_OK)
            {
                throw new InvalidOperationException("The in-memory join store could not be locked read-only.");
            }

            _readOnly = true;
        }

        if (HasTrailingStatement(sql))
        {
            throw new InvalidOperationException(TrailingStatementRejection);
        }
    }

    /// <summary>
    /// True when SQLite's own tokenizer finds anything but whitespace and comments after the first statement — the
    /// AST gate's parser can disagree on where that statement ends. Compiles, never steps, and finalizes both
    /// statements. A first statement that does not compile (or that the authorizer denies) reports false, so
    /// running it raises SQLite's own error; a tail that does not compile counts as a statement.
    /// </summary>
    private bool HasTrailingStatement(string sql)
    {
        var rc = raw.sqlite3_prepare_v2(_connection.Handle, sql, out var first, out var tail);
        raw.sqlite3_finalize(first);
        if (rc != raw.SQLITE_OK || string.IsNullOrWhiteSpace(tail))
        {
            return false;
        }

        var tailRc = raw.sqlite3_prepare_v2(_connection.Handle, tail, out var second, out _);
        var tailHasStatement = !second.IsInvalid;
        raw.sqlite3_finalize(second);

        return tailRc != raw.SQLITE_OK || tailHasStatement;
    }

    private async Task ExecuteLoadStatementAsync(string sql, SqliteTransaction transaction)
    {
        await using var command = CreateLoadCommand(sql, transaction);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// A command for one load statement. Microsoft.Data.Sqlite runs every statement in a command's text, so the
    /// text must be exactly one statement to SQLite's own tokenizer — a load never runs as a batch.
    /// </summary>
    private SqliteCommand CreateLoadCommand(string sql, SqliteTransaction transaction)
    {
        if (HasTrailingStatement(sql))
        {
            throw new InvalidOperationException("A table load must be a single SQL statement.");
        }

        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;

        return command;
    }

    /// <summary>
    /// <paramref name="name"/> as a double-quoted SQLite identifier (embedded quotes doubled), so it names exactly
    /// that table or column — <c>[name]</c> in a query names the same one. A NUL has no representation: SQLite ends
    /// statement text at the first NUL, so such a name is refused before anything runs.
    /// </summary>
    private static string QuoteIdentifier(string name)
    {
        if (name.Contains('\0'))
        {
            throw new InvalidOperationException("A table or column name containing a NUL character cannot be loaded into the in-memory join store.");
        }

        return "\"" + name.Replace("\"", "\"\"") + "\"";
    }

    private static string PositionalParameter(int index) =>
        "@p" + index.ToString(CultureInfo.InvariantCulture);

    private static bool IsSchemaTable(utf8z table) =>
        table.utf8_to_string() is "sqlite_master" or "sqlite_schema";

    private static bool IsCancellation(Exception ex) =>
        ex is OperationCanceledException or SqliteException { SqliteErrorCode: raw.SQLITE_INTERRUPT };
}

public class InMemoryDatabaseAnalysis
{
    public Dictionary<string, InMemoryTableInfo> Tables { get; set; } = new();
    public int TotalTables { get; set; }
    public int TotalRows { get; set; }
}

public class InMemoryTableInfo
{
    public string TableName { get; set; } = null!;
    public int RowCount { get; set; }
    public int ColumnCount { get; set; }
    public string SourceProject { get; set; } = null!;
    public string SourceDatabaseEngine { get; set; } = null!;
}
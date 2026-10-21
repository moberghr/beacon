using Microsoft.Extensions.Logging;
using Beacon.Core.Adapters;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Recipients;
using Beacon.Core.Services.Validation;

namespace Beacon.Core.Services;

public class VirtualTableManager : IDisposable
{
    private readonly Dictionary<string, List<IDictionary<string, object?>>> _virtualTables = new();
    private readonly Dictionary<string, ProjectInfo> _tableProjectInfo = new();
    private readonly ILogger<VirtualTableManager> _logger;

    public VirtualTableManager(ILogger<VirtualTableManager> logger)
    {
        _logger = logger;
    }

    /// <summary>How long the final query may run on the in-memory store. Settable for tests.</summary>
    internal int StatementTimeoutSeconds { get; init; } = Constants.Query.StatementTimeoutSeconds;

    public void AddVirtualTable(string name, List<IDictionary<string, object?>> data, ProjectInfo projectInfo)
    {
        _virtualTables[name] = data;
        _tableProjectInfo[name] = projectInfo;

        _logger.LogDebug("Added virtual table {VirtualTableName} with {RowCount} rows from project {DataSourceName} ({DatabaseEngine})",
            name, data.Count, projectInfo.Name, projectInfo.DatabaseEngine);
    }

    public void ClearVirtualTables()
    {
        _virtualTables.Clear();
        _tableProjectInfo.Clear();
        _logger.LogDebug("Cleared all virtual tables and project info");
    }

    /// <summary>
    /// The final query of a multi-step run on the in-memory SQLite store. <paramref name="gate"/> judges the statement as it
    /// runs (after <c>@resultN</c> translation) before any table is loaded — a blocked statement throws, so a statement like
    /// ATTACH never reaches SQLite (it would create files on the host) — and its <see cref="SqlGateReport.FinalSql"/> (the
    /// row cap the gate placed) is what runs, within <see cref="StatementTimeoutSeconds"/> and the caller's cancellation,
    /// reading at most <paramref name="maxRows"/> rows (a result holding exactly that many may have had more). A statement
    /// that runs out of time is reported with <see cref="QueryResult.TimedOut"/> and no rows — never a result to act on.
    /// </summary>
    public async Task<QueryResult> ExecuteFinalQueryWithInMemoryDatabase(
        string finalQuery,
        Func<string, SqlGateReport> gate,
        ILogger<InMemoryDatabaseManager> inMemoryDbLogger,
        int maxRows,
        CancellationToken cancellationToken)
    {
        using var inMemoryDb = new InMemoryDatabaseManager(inMemoryDbLogger);

        // Translate the final query to use actual table names
        var translatedQuery = inMemoryDb.TranslateFinalQuery(finalQuery);

        // The final query is stored or LLM-written SQL — gate the text that runs (§1.5) before anything is loaded.
        var report = gate(translatedQuery);
        if (report.Blocked)
        {
            throw new InvalidOperationException(report.BlockReason ?? "The final query was rejected.");
        }

        _logger.LogInformation("Executing final query using in-memory SQLite database with {TableCount} virtual tables",
            _virtualTables.Count);

        // Create tables in SQLite for each virtual table
        foreach (var kvp in _virtualTables)
        {
            var tableName = kvp.Key.Substring(1); // Remove @ prefix
            var data = kvp.Value;
            var projectInfo = _tableProjectInfo[kvp.Key];

            await inMemoryDb.CreateTableFromResults(tableName, data, projectInfo);
        }

        // Execute the query against SQLite
        var (results, executionTimeMs, timedOut) = await inMemoryDb.ExecuteQueryAsync(
            report.FinalSql,
            StatementTimeoutSeconds,
            maxRows,
            cancellationToken);

        // Log database analysis
        var analysis = inMemoryDb.AnalyzeDatabase();
        _logger.LogInformation("In-memory database analysis: {TotalTables} tables, {TotalRows} total rows",
            analysis.TotalTables, analysis.TotalRows);

        return new QueryResult
        {
            QueryResults = System.Text.Json.JsonSerializer.Serialize(results.Take(20)),
            TotalRecords = results.Count,
            DataSourceName = "In-Memory SQLite Database",
            SqlQuery = finalQuery, // Show original query with virtual table references
            AllRecords = results,
            TopRecords = results.Take(20).ToList(),
            SubscriptionName = "Cross-Project Query Chain (SQLite)",
            SubscriptionId = null,
            ExecutionTimeMs = executionTimeMs,
            TimedOut = timedOut,
            Recipients = new List<RecipientData>()
        };
    }

    /// <summary>
    /// <see cref="ExecuteFinalQueryWithInMemoryDatabase(string, Func{string, SqlGateReport}, ILogger{InMemoryDatabaseManager}, int, CancellationToken)"/>
    /// reading every row the final query returns.
    /// </summary>
    [Obsolete("Pass the row cap: ExecuteFinalQueryWithInMemoryDatabase(finalQuery, gate, inMemoryDbLogger, maxRows, cancellationToken).")]
    public Task<QueryResult> ExecuteFinalQueryWithInMemoryDatabase(
        string finalQuery,
        Func<string, SqlGateReport> gate,
        ILogger<InMemoryDatabaseManager> inMemoryDbLogger,
        CancellationToken cancellationToken) =>
        ExecuteFinalQueryWithInMemoryDatabase(finalQuery, gate, inMemoryDbLogger, int.MaxValue, cancellationToken);

    /// <summary>
    /// The final query of a multi-step preview, one page at a time: gated and loaded into SQLite as for
    /// <see cref="ExecuteFinalQueryWithInMemoryDatabase"/>, then only the page is read back, the page and count statements
    /// within <see cref="StatementTimeoutSeconds"/> and the caller's cancellation together. The page and count rewrites
    /// pass the same gate. A final query that runs out of time throws <see cref="TimeoutException"/>.
    /// </summary>
    public async Task<SqlResultPage> ExecuteFinalQueryPagedAsync(
        string finalQuery,
        Func<string, SqlGateReport> gate,
        ILogger<InMemoryDatabaseManager> inMemoryDbLogger,
        Helpers.ListRequest paging,
        CancellationToken cancellationToken)
    {
        var translatedQuery = InMemoryDatabaseManager.TranslateResultReferences(finalQuery);

        // Gate the text that runs (§1.5) before any virtual table is loaded, as the non-paged path does.
        var rejection = Rejection(gate, translatedQuery);
        if (rejection != null)
        {
            throw new InvalidOperationException(rejection);
        }

        using var inMemoryDb = new InMemoryDatabaseManager(inMemoryDbLogger);

        foreach (var kvp in _virtualTables)
        {
            await inMemoryDb.CreateTableFromResults(kvp.Key.Substring(1), kvp.Value, _tableProjectInfo[kvp.Key]);
        }

        return await inMemoryDb.ExecutePagedAsync(
            translatedQuery,
            paging,
            x => Rejection(gate, x),
            StatementTimeoutSeconds,
            cancellationToken);
    }

    private static string? Rejection(Func<string, SqlGateReport> gate, string sql)
    {
        var report = gate(sql);

        return report.Blocked ? report.BlockReason ?? "The final query was rejected." : null;
    }

    public void Dispose()
    {
        ClearVirtualTables();
        GC.SuppressFinalize(this);
    }
}

public class ProjectInfo
{
    public required string Name { get; init; }
    public required string DatabaseEngine { get; init; }
    public required DatabaseEngineType DatabaseEngineType { get; init; }
}

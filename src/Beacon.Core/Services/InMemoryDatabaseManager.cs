using System.Data;
using System.Globalization;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Services;

public class InMemoryDatabaseManager : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<InMemoryDatabaseManager> _logger;
    private readonly Dictionary<string, ProjectInfo> _tableProjectInfo = new();
    private bool _disposed;

    public InMemoryDatabaseManager(ILogger<InMemoryDatabaseManager> logger)
    {
        _logger = logger;
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _logger.LogDebug("Created in-memory SQLite database connection");
    }

    public async Task CreateTableFromResults(string tableName, List<IDictionary<string, object?>> data, ProjectInfo sourceProject)
    {
        if (!data.Any())
        {
            _logger.LogDebug("Skipping table creation for {TableName} - no data provided", tableName);
            return;
        }

        _tableProjectInfo[tableName] = sourceProject;

        // Analyze data structure
        var firstRow = data.First();
        var columnDefinitions = new List<string>();

        foreach (var kvp in firstRow)
        {
            var columnName = kvp.Key;
            var columnType = InferSqliteType(kvp.Value);
            columnDefinitions.Add($"[{columnName}] {columnType}");
        }

        // Create table
        var createTableSql = $"CREATE TABLE [{tableName}] ({string.Join(", ", columnDefinitions)})";

        _logger.LogDebug("Creating table {TableName} with {ColumnCount} columns from {SourceProject} ({DatabaseEngine})",
            tableName, columnDefinitions.Count, sourceProject.Name, sourceProject.DatabaseEngine);

        await _connection.ExecuteAsync(createTableSql);

        // Bulk insert data
        await BulkInsertData(tableName, data);

        _logger.LogInformation("Created table {TableName} with {RowCount} rows from {SourceProject}",
            tableName, data.Count, sourceProject.Name);
    }

    private async Task BulkInsertData(string tableName, List<IDictionary<string, object?>> data)
    {
        if (!data.Any()) return;

        var firstRow = data.First();
        var columnNames = firstRow.Keys.ToList();
        var placeholders = string.Join(", ", columnNames.Select(c => "@" + c));

        var insertSql = $"INSERT INTO [{tableName}] ({string.Join(", ", columnNames.Select(c => $"[{c}]"))}) VALUES ({placeholders})";

        // Use transaction for better performance
        using var transaction = _connection.BeginTransaction();

        foreach (var row in data)
        {
            var parameters = new DynamicParameters();
            foreach (var kvp in row)
            {
                parameters.Add("@" + kvp.Key, ConvertValueForSqlite(kvp.Value));
            }

            await _connection.ExecuteAsync(insertSql, parameters, transaction);
        }

        await transaction.CommitAsync();
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
        int? timeoutSeconds = null)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var timeoutCts = timeoutSeconds.HasValue
                ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds.Value))
                : new CancellationTokenSource();

            var commandDefinition = new CommandDefinition(
                commandText: sql,
                commandTimeout: timeoutSeconds,
                cancellationToken: timeoutCts.Token
            );

            var dapperRows = await _connection.QueryAsync(commandDefinition);

            stopwatch.Stop();
            var executionTimeMs = stopwatch.Elapsed.TotalMilliseconds;

            var results = dapperRows.Select(x => (IDictionary<string, object?>)x).ToList();

            _logger.LogDebug("Executed SQLite query in {ExecutionTimeMs}ms, returned {ResultCount} rows",
                executionTimeMs, results.Count);

            return (results, executionTimeMs, false);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
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

    public string TranslateFinalQuery(string originalQuery)
    {
        // Replace @result references with actual table names
        var translatedQuery = originalQuery;

        // Find all @result patterns and replace with table names
        var virtualTablePattern = new System.Text.RegularExpressions.Regex(@"@result(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var matches = virtualTablePattern.Matches(originalQuery);

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var virtualTableRef = match.Value.ToLower();
            var stepNumber = match.Groups[1].Value;
            var tableName = $"result{stepNumber}";

            translatedQuery = translatedQuery.Replace(match.Value, $"[{tableName}]", StringComparison.OrdinalIgnoreCase);

            _logger.LogDebug("Translated {VirtualRef} to [{TableName}]", match.Value, tableName);
        }

        return translatedQuery;
    }

    public InMemoryDatabaseAnalysis AnalyzeDatabase()
    {
        var analysis = new InMemoryDatabaseAnalysis();

        // Get table information
        var tableInfoSql = "SELECT name FROM sqlite_master WHERE type='table'";
        var tables = _connection.Query<string>(tableInfoSql).ToList();

        foreach (var tableName in tables)
        {
            var rowCountSql = $"SELECT COUNT(*) FROM [{tableName}]";
            var rowCount = _connection.QuerySingle<int>(rowCountSql);

            var columnInfoSql = $"PRAGMA table_info([{tableName}])";
            var columnInfo = _connection.Query(columnInfoSql).ToList();

            var projectInfo = _tableProjectInfo.GetValueOrDefault(tableName);

            analysis.Tables[tableName] = new InMemoryTableInfo
            {
                TableName = tableName,
                RowCount = rowCount,
                ColumnCount = columnInfo.Count,
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
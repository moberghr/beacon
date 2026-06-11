using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Beacon.Core.Adapters;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Recipients;

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

    public void AddVirtualTable(string name, List<IDictionary<string, object?>> data, ProjectInfo projectInfo)
    {
        _virtualTables[name] = data;
        _tableProjectInfo[name] = projectInfo;

        _logger.LogDebug("Added virtual table {VirtualTableName} with {RowCount} rows from project {DataSourceName} ({DatabaseEngine})",
            name, data.Count, projectInfo.Name, projectInfo.DatabaseEngine);
    }

    public string ParseAndReplaceVirtualTables(string sql, DatabaseEngineType targetDatabaseEngine)
    {
        var virtualTablePattern = new Regex(@"@result(\d+)", RegexOptions.IgnoreCase);
        var matches = virtualTablePattern.Matches(sql);

        if (!matches.Any())
        {
            return sql;
        }

        var referencedTables = matches.Cast<Match>()
            .Select(m => m.Value.ToLower())
            .Distinct()
            .ToList();

        // Log cross-database scenario
        var sourceDatabases = referencedTables
            .Where(t => _tableProjectInfo.ContainsKey(t))
            .Select(t => _tableProjectInfo[t].DatabaseEngine)
            .Distinct()
            .ToList();

        if (sourceDatabases.Count > 1 || sourceDatabases.Any(db => db != targetDatabaseEngine.ToString()))
        {
            _logger.LogInformation("Cross-database query detected: combining data from {SourceEngines} into {TargetEngine}",
                string.Join(", ", sourceDatabases), targetDatabaseEngine);
        }

        // Validate all referenced tables exist
        foreach (var tableName in referencedTables)
        {
            if (!_virtualTables.ContainsKey(tableName))
            {
                throw new BeaconException($"Virtual table {tableName} is referenced but not available. Available: {string.Join(", ", _virtualTables.Keys)}");
            }
        }

        return targetDatabaseEngine switch
        {
            DatabaseEngineType.PostgreSQL => BuildPostgreSqlWithCtes(sql, referencedTables),
            DatabaseEngineType.MSSQL => BuildSqlServerWithCtes(sql, referencedTables),
            DatabaseEngineType.MySQL => BuildMySqlWithCtes(sql, referencedTables),
            _ => throw new BeaconException($"Virtual table support not implemented for {targetDatabaseEngine}")
        };
    }

    private string BuildSqlServerWithCtes(string sql, List<string> referencedTables)
    {
        var cteBuilder = new StringBuilder("WITH ");
        var cteList = new List<string>();

        foreach (var tableName in referencedTables)
        {
            var data = _virtualTables[tableName];
            var projectInfo = _tableProjectInfo[tableName];
            var cteName = tableName.Substring(1); // Remove @

            var cte = BuildSqlServerCte(cteName, data, projectInfo);
            cteList.Add(cte);
        }

        cteBuilder.Append(string.Join(",\n", cteList));
        cteBuilder.AppendLine();
        cteBuilder.Append(sql);

        return cteBuilder.ToString();
    }

    private string BuildPostgreSqlWithCtes(string sql, List<string> referencedTables)
    {
        var cteBuilder = new StringBuilder("WITH ");
        var cteList = new List<string>();

        foreach (var tableName in referencedTables)
        {
            var data = _virtualTables[tableName];
            var projectInfo = _tableProjectInfo[tableName];
            var cteName = tableName.Substring(1); // Remove @

            var cte = BuildPostgreSqlCte(cteName, data, projectInfo);
            cteList.Add(cte);
        }

        cteBuilder.Append(string.Join(",\n", cteList));
        cteBuilder.AppendLine();
        cteBuilder.Append(sql);

        return cteBuilder.ToString();
    }

    private string BuildMySqlWithCtes(string sql, List<string> referencedTables)
    {
        var cteBuilder = new StringBuilder("WITH ");
        var cteList = new List<string>();

        foreach (var tableName in referencedTables)
        {
            var data = _virtualTables[tableName];
            var projectInfo = _tableProjectInfo[tableName];
            var cteName = tableName.Substring(1); // Remove @

            var cte = BuildMySqlCte(cteName, data, projectInfo);
            cteList.Add(cte);
        }

        cteBuilder.Append(string.Join(",\n", cteList));
        cteBuilder.AppendLine();
        cteBuilder.Append(sql);

        return cteBuilder.ToString();
    }

    private string BuildSqlServerCte(string cteName, List<IDictionary<string, object?>> data, ProjectInfo sourceProject)
    {
        if (!data.Any())
        {
            return $@"{cteName} AS (
    -- Empty result set from {sourceProject.Name} ({sourceProject.DatabaseEngine})
    SELECT NULL as EmptyTable WHERE 1=0
)";
        }

        var columns = data.First().Keys.ToList();
        var valueRows = data.Take(1000).Select(row => // Limit to 1000 rows for performance
            "(" + string.Join(", ", columns.Select(col => FormatValueForSqlServer(row[col]))) + ")"
        );

        return $@"{cteName} AS (
    -- Data from {sourceProject.Name} ({sourceProject.DatabaseEngine}) - {data.Count} rows
    SELECT {string.Join(", ", columns.Select(x => QuoteColumn(x, DatabaseEngineType.MSSQL)))}
    FROM (VALUES {string.Join(",\n           ", valueRows)}) AS t({string.Join(", ", columns.Select(x => QuoteColumn(x, DatabaseEngineType.MSSQL)))})
)";
    }

    private string BuildPostgreSqlCte(string cteName, List<IDictionary<string, object?>> data, ProjectInfo sourceProject)
    {
        if (!data.Any())
        {
            return $@"{cteName} AS (
    -- Empty result set from {sourceProject.Name} ({sourceProject.DatabaseEngine})
    SELECT NULL::text as EmptyTable WHERE false
)";
        }

        var columns = data.First().Keys.ToList();
        var valueRows = data.Take(1000).Select(row => // Limit to 1000 rows for performance
            "(" + string.Join(", ", columns.Select(col => FormatValueForPostgreSql(row[col]))) + ")"
        );

        return $@"{cteName} AS (
    -- Data from {sourceProject.Name} ({sourceProject.DatabaseEngine}) - {data.Count} rows
    SELECT {string.Join(", ", columns.Select(x => QuoteColumn(x, DatabaseEngineType.PostgreSQL)))}
    FROM (VALUES {string.Join(",\n           ", valueRows)}) AS t({string.Join(", ", columns.Select(x => QuoteColumn(x, DatabaseEngineType.PostgreSQL)))})
)";
    }

    private string BuildMySqlCte(string cteName, List<IDictionary<string, object?>> data, ProjectInfo sourceProject)
    {
        if (!data.Any())
        {
            return $@"{cteName} AS (
    -- Empty result set from {sourceProject.Name} ({sourceProject.DatabaseEngine})
    SELECT NULL as EmptyTable WHERE FALSE
)";
        }

        var columns = data.First().Keys.ToList();
        var valueRows = data.Take(1000).Select(row => // Limit to 1000 rows for performance
            "(" + string.Join(", ", columns.Select(col => FormatValueForMySql(row[col]))) + ")"
        );

        return $@"{cteName} AS (
    -- Data from {sourceProject.Name} ({sourceProject.DatabaseEngine}) - {data.Count} rows
    SELECT {string.Join(", ", columns.Select(x => QuoteColumn(x, DatabaseEngineType.MySQL)))}
    FROM (VALUES {string.Join(",\n           ", valueRows)}) AS t({string.Join(", ", columns.Select(x => QuoteColumn(x, DatabaseEngineType.MySQL)))})
)";
    }

    // §1.10 — Column names are interpolated into the CTE column lists below and cannot be
    // parameterized, so each is whitelist-validated via SqlIdentifierGuard before quoting and the
    // embedded quote char is doubled per dialect as defence-in-depth.
    private static string QuoteColumn(string column, DatabaseEngineType engineType)
    {
        SqlIdentifierGuard.Validate(column, "column");

        return engineType switch
        {
            DatabaseEngineType.PostgreSQL => $"\"{SqlIdentifierGuard.EscapeQuote(column, '"')}\"",
            DatabaseEngineType.MSSQL => $"[{column.Replace("]", "]]")}]",
            DatabaseEngineType.MySQL => $"`{SqlIdentifierGuard.EscapeQuote(column, '`')}`",
            _ => column
        };
    }

    // Enhanced formatting methods that handle cross-database data type differences
    private string FormatValueForSqlServer(object? value)
    {
        return value switch
        {
            null => "NULL",
            string s => $"N'{s.Replace("'", "''")}'",
            DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss.fff}'",
            DateTimeOffset dto => $"'{dto:yyyy-MM-dd HH:mm:ss.fff zzz}'",
            bool b => b ? "CAST(1 AS BIT)" : "CAST(0 AS BIT)",
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            double dbl => dbl.ToString(CultureInfo.InvariantCulture),
            float f => f.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(),
            long l => l.ToString(),
            Guid g => $"'{g}'",
            byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
            _ => $"N'{value.ToString()?.Replace("'", "''")}'"
        };
    }

    private string FormatValueForPostgreSql(object? value)
    {
        return value switch
        {
            null => "NULL",
            string s => $"'{s.Replace("'", "''")}'",
            DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'::timestamp",
            DateTimeOffset dto => $"'{dto:yyyy-MM-dd HH:mm:ss zzz}'::timestamptz",
            bool b => b ? "true" : "false",
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            double dbl => dbl.ToString(CultureInfo.InvariantCulture),
            float f => f.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(),
            long l => l.ToString(),
            Guid g => $"'{g}'::uuid",
            byte[] bytes => $"'\\x{Convert.ToHexString(bytes)}'::bytea",
            _ => $"'{value.ToString()?.Replace("'", "''")}'"
        };
    }

    private string FormatValueForMySql(object? value)
    {
        return value switch
        {
            null => "NULL",
            string s => $"'{s.Replace("'", "\\'").Replace("\\", "\\\\")}'",
            DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
            bool b => b ? "1" : "0",
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            double dbl => dbl.ToString(CultureInfo.InvariantCulture),
            float f => f.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(),
            long l => l.ToString(),
            Guid g => $"'{g}'",
            byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
            _ => $"'{value.ToString()?.Replace("'", "\\'").Replace("\\", "\\\\")}'"
        };
    }

    public CrossDatabaseAnalysisResult AnalyzeCrossDatabase()
    {
        var analysis = new CrossDatabaseAnalysisResult();

        foreach (var kvp in _tableProjectInfo)
        {
            var tableName = kvp.Key;
            var projectInfo = kvp.Value;
            var rowCount = _virtualTables[tableName].Count;

            analysis.TableAnalysis[tableName] = new VirtualTableAnalysis
            {
                TableName = tableName,
                SourceProject = projectInfo.Name,
                SourceDatabaseEngine = projectInfo.DatabaseEngine,
                RowCount = rowCount,
                ColumnCount = _virtualTables[tableName].FirstOrDefault()?.Count ?? 0,
                EstimatedMemoryUsageMB = EstimateMemoryUsage(_virtualTables[tableName])
            };
        }

        analysis.TotalTables = _virtualTables.Count;
        analysis.UniqueDatabaseEngines = _tableProjectInfo.Values.Select(p => p.DatabaseEngine).Distinct().ToList();
        analysis.TotalRowsInMemory = _virtualTables.Values.Sum(v => v.Count);
        analysis.EstimatedTotalMemoryUsageMB = analysis.TableAnalysis.Values.Sum(t => t.EstimatedMemoryUsageMB);

        return analysis;
    }

    private double EstimateMemoryUsage(List<IDictionary<string, object?>> data)
    {
        if (!data.Any()) return 0;

        // Rough estimation: average 50 bytes per value
        var totalValues = data.Sum(row => row.Count);
        return (totalValues * 50) / (1024.0 * 1024.0); // Convert to MB
    }

    public void ClearVirtualTables()
    {
        _virtualTables.Clear();
        _tableProjectInfo.Clear();
        _logger.LogDebug("Cleared all virtual tables and project info");
    }

    public async Task<QueryResult> ExecuteFinalQueryWithInMemoryDatabase(
        string finalQuery,
        ILogger<InMemoryDatabaseManager> inMemoryDbLogger,
        CancellationToken cancellationToken)
    {
        using var inMemoryDb = new InMemoryDatabaseManager(inMemoryDbLogger);

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

        // Translate the final query to use actual table names
        var translatedQuery = inMemoryDb.TranslateFinalQuery(finalQuery);

        _logger.LogDebug("Translated query: {TranslatedQuery}", translatedQuery);

        // Execute the query against SQLite
        var (results, executionTimeMs, timedOut) = await inMemoryDb.ExecuteQueryAsync(translatedQuery);

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

public class CrossDatabaseAnalysisResult
{
    public Dictionary<string, VirtualTableAnalysis> TableAnalysis { get; set; } = new();
    public int TotalTables { get; set; }
    public List<string> UniqueDatabaseEngines { get; set; } = new();
    public int TotalRowsInMemory { get; set; }
    public double EstimatedTotalMemoryUsageMB { get; set; }
}

public class VirtualTableAnalysis
{
    public string TableName { get; set; } = null!;
    public string SourceProject { get; set; } = null!;
    public string SourceDatabaseEngine { get; set; } = null!;
    public int RowCount { get; set; }
    public int ColumnCount { get; set; }
    public double EstimatedMemoryUsageMB { get; set; }
}
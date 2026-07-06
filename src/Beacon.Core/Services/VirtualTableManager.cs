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
using System.Data.Common;
using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers;
using Semantico.Core.Models;
using Semantico.Core.Models.Providers;

namespace Semantico.Core.Services.Providers;

internal class DatabaseProvider(
    IEncryptionService encryptionService,
    ILogger<DatabaseProvider> logger) : IDataSourceProvider
{
    public DataSourceType SupportedType => DataSourceType.Database;

    public string GetQueryLanguageName() => "SQL";

    public async Task<ConnectionTestResult> TestConnectionAsync(
        DataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!dataSource.DatabaseEngineType.HasValue)
                throw new SemanticoException("DatabaseEngineType is required for Database data sources");

            var connectionString = encryptionService.Decrypt(dataSource.EncryptedConnectionData);
            await using var connection = DbConnectionFactory.CreateConnection(
                dataSource.DatabaseEngineType.Value,
                connectionString);

            await connection.OpenAsync(cancellationToken);

            stopwatch.Stop();

            return new ConnectionTestResult
            {
                Success = true,
                TestDurationMs = stopwatch.Elapsed.TotalMilliseconds,
                ConnectionInfo = new Dictionary<string, object?>
                {
                    ["ServerVersion"] = connection.ServerVersion,
                    ["Database"] = connection.Database,
                    ["DataSource"] = connection.DataSource,
                    ["State"] = connection.State.ToString()
                }
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Connection test failed for database data source {DataSourceId}", dataSource.Id);

            stopwatch.Stop();

            return new ConnectionTestResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                TestDurationMs = stopwatch.Elapsed.TotalMilliseconds
            };
        }
    }

    public async Task<ProviderQueryResult> ExecuteQueryAsync(
        DataSource dataSource,
        string query,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!dataSource.DatabaseEngineType.HasValue)
                throw new SemanticoException("DatabaseEngineType is required for Database data sources");

            var connectionString = encryptionService.Decrypt(dataSource.EncryptedConnectionData);
            await using var connection = DbConnectionFactory.CreateConnection(
                dataSource.DatabaseEngineType.Value,
                connectionString);

            await connection.OpenAsync(cancellationToken);

            var commandDefinition = new CommandDefinition(
                query,
                parameters,
                cancellationToken: cancellationToken,
                commandTimeout: 120);

            var result = await connection.QueryAsync(commandDefinition);
            var rows = ConvertDapperResultsToRows(result.AsList());

            stopwatch.Stop();

            return new ProviderQueryResult
            {
                Rows = rows,
                TotalRows = rows.Count,
                ExecutionTimeMs = stopwatch.Elapsed.TotalMilliseconds,
                Success = true,
                Metadata = new Dictionary<string, object?>
                {
                    ["DatabaseEngine"] = dataSource.DatabaseEngineType.Value.ToString()
                }
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Query execution failed for database data source {DataSourceId}", dataSource.Id);

            stopwatch.Stop();

            return new ProviderQueryResult
            {
                Rows = new List<Dictionary<string, object?>>(),
                TotalRows = 0,
                ExecutionTimeMs = stopwatch.Elapsed.TotalMilliseconds,
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    public Task<DataSourceMetadata> GetMetadataAsync(
        DataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        // Database metadata is handled by DatabaseMetadataService
        // This provider method is for future extensibility
        throw new NotImplementedException(
            "Database metadata should be retrieved via IDatabaseMetadataService");
    }

    public async Task<QueryValidationResult> ValidateQueryAsync(
        DataSource dataSource,
        string query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!dataSource.DatabaseEngineType.HasValue)
            {
                return new QueryValidationResult
                {
                    IsValid = false,
                    Errors = new List<string> { "DatabaseEngineType is required for Database data sources" }
                };
            }

            // Basic validation: try to prepare the query without executing
            var connectionString = encryptionService.Decrypt(dataSource.EncryptedConnectionData);
            await using var connection = DbConnectionFactory.CreateConnection(
                dataSource.DatabaseEngineType.Value,
                connectionString);

            await connection.OpenAsync(cancellationToken);

            // For PostgreSQL, we can use EXPLAIN to validate syntax
            if (dataSource.DatabaseEngineType == DatabaseEngineType.PostgreSQL)
            {
                var explainQuery = $"EXPLAIN {query}";
                await connection.QueryAsync(new CommandDefinition(
                    explainQuery,
                    cancellationToken: cancellationToken,
                    commandTimeout: 30));
            }

            return new QueryValidationResult
            {
                IsValid = true
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Query validation failed for database data source {DataSourceId}", dataSource.Id);

            return new QueryValidationResult
            {
                IsValid = false,
                Errors = new List<string> { ex.Message }
            };
        }
    }

    private static List<Dictionary<string, object?>> ConvertDapperResultsToRows(IList<dynamic> dapperResults)
    {
        var rows = new List<Dictionary<string, object?>>();

        foreach (var row in dapperResults)
        {
            var dict = new Dictionary<string, object?>();

            if (row is IDictionary<string, object> rowDict)
            {
                foreach (var kvp in rowDict)
                {
                    dict[kvp.Key] = kvp.Value;
                }
            }

            rows.Add(dict);
        }

        return rows;
    }
}

using System.Data.Common;
using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services.Validation;

namespace Beacon.Core.Services.Providers;

internal class DatabaseProvider(
    IEncryptionService encryptionService,
    SqlReadOnlyAstValidator readOnlyValidator,
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
                throw new BeaconException("DatabaseEngineType is required for Database data sources");

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

    public Task<ProviderQueryResult> ExecuteQueryAsync(
        DataSource dataSource,
        string query,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        return ExecuteQueryCoreAsync(dataSource, query, parameters, enforceReadOnly: false, cancellationToken);
    }

    public Task<ProviderQueryResult> ExecuteReadOnlyQueryAsync(
        DataSource dataSource,
        string query,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        return ExecuteQueryCoreAsync(dataSource, query, parameters, enforceReadOnly: true, cancellationToken);
    }

    // Honest capability report: the database-level backstop exists ONLY for engines with a working
    // read-only transaction path here (PostgreSQL today — see SupportsReadOnlyTransaction). For every
    // other engine ExecuteReadOnlyQueryAsync degrades to plain execution and callers rely on the
    // parser gates alone.
    public bool SupportsDatabaseReadOnlyEnforcement(DatabaseEngineType? engine)
    {
        return engine.HasValue && SupportsReadOnlyTransaction(engine.Value);
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

            // Read-only enforcement (§1.5): reject anything that is not a single SELECT before the
            // engine-specific syntax dry-run below.
            var readOnlyError = readOnlyValidator.Validate(query, ResolveDialect(dataSource.DatabaseEngineType.Value));
            if (readOnlyError != null)
            {
                return new QueryValidationResult
                {
                    IsValid = false,
                    Errors = new List<string> { readOnlyError }
                };
            }

            // Engines without a dry-run strategy must NOT fall through as valid — nothing would have
            // been checked. Return an explicit skipped result the caller can distinguish (and must not
            // repair against). Checked BEFORE opening a connection: there is nothing to connect for.
            if (!SupportsDryRunValidation(dataSource.DatabaseEngineType.Value))
            {
                return new QueryValidationResult
                {
                    IsValid = false,
                    Skipped = true,
                    Errors = new List<string>
                    {
                        $"Provider dry-run validation is not supported for engine {dataSource.DatabaseEngineType.Value} — the query was not validated against the live database."
                    }
                };
            }

            // Basic validation: try to prepare the query without executing
            var connectionString = encryptionService.Decrypt(dataSource.EncryptedConnectionData);
            await using var connection = DbConnectionFactory.CreateConnection(
                dataSource.DatabaseEngineType.Value,
                connectionString);

            await connection.OpenAsync(cancellationToken);

            // Engine-specific dry-run: validates syntax and column binding without executing the query.
            switch (dataSource.DatabaseEngineType)
            {
                case DatabaseEngineType.PostgreSQL:
                case DatabaseEngineType.MySQL:
                case DatabaseEngineType.Snowflake:
                    await connection.QueryAsync(new CommandDefinition(
                        $"EXPLAIN {query}",
                        cancellationToken: cancellationToken,
                        commandTimeout: 30));
                    break;

                case DatabaseEngineType.MSSQL:
                case DatabaseEngineType.AzureSynapse:
                    await connection.QueryAsync(new CommandDefinition(
                        "sp_describe_first_result_set @tsql",
                        new { tsql = query },
                        cancellationToken: cancellationToken,
                        commandTimeout: 30));
                    break;
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

    private async Task<ProviderQueryResult> ExecuteQueryCoreAsync(
        DataSource dataSource,
        string query,
        Dictionary<string, object?> parameters,
        bool enforceReadOnly,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!dataSource.DatabaseEngineType.HasValue)
            {
                throw new BeaconException("DatabaseEngineType is required for Database data sources");
            }

            var connectionString = encryptionService.Decrypt(dataSource.EncryptedConnectionData);
            await using var connection = DbConnectionFactory.CreateConnection(
                dataSource.DatabaseEngineType.Value,
                connectionString);

            await connection.OpenAsync(cancellationToken);

            // §1.5 backstop — parser-level read-only enforcement alone is bypassable (SQL injection past
            // the regex/AST gates), so the database itself rejects writes: any write attempt inside a
            // READ ONLY transaction fails server-side (PostgreSQL 25006 read_only_sql_transaction),
            // regardless of what the parsers missed.
            var useReadOnlyTransaction = enforceReadOnly
                && SupportsDatabaseReadOnlyEnforcement(dataSource.DatabaseEngineType);

            if (useReadOnlyTransaction)
            {
                // Session-scoped outer belt: SET TRANSACTION READ ONLY alone is transaction-scoped —
                // an injected "COMMIT; <write>" ends the transaction and the write runs autocommit.
                // With default_transaction_read_only = on, every transaction on this session
                // (implicit autocommit ones included) is read-only, so that write fails server-side
                // too. Runs ON THE CONNECTION (no transaction) before the transaction begins. Npgsql
                // resets session state when the connection returns to the pool, so no manual cleanup
                // is needed here.
                await connection.ExecuteAsync(new CommandDefinition(
                    "SET default_transaction_read_only = on",
                    cancellationToken: cancellationToken));
            }

            await using var transaction = useReadOnlyTransaction
                ? await connection.BeginTransactionAsync(cancellationToken)
                : null;

            if (transaction != null)
            {
                // Inner belt: the explicit transaction is additionally opened READ ONLY.
                await connection.ExecuteAsync(new CommandDefinition(
                    "SET TRANSACTION READ ONLY",
                    transaction: transaction,
                    cancellationToken: cancellationToken));
            }

            var commandDefinition = new CommandDefinition(
                query,
                parameters,
                transaction: transaction,
                cancellationToken: cancellationToken,
                commandTimeout: 120);

            var result = await connection.QueryAsync(commandDefinition);
            var rows = ConvertDapperResultsToRows(result.AsList());

            if (transaction != null)
            {
                // Reads inside a READ ONLY transaction commit fine.
                await transaction.CommitAsync(cancellationToken);
            }

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

    // PostgreSQL supports the session-level default_transaction_read_only backstop plus
    // SET TRANSACTION READ ONLY as the first statement of an open transaction. MySQL 5.7+ supports
    // read-only transactions too — deferred: requires START TRANSACTION READ ONLY plumbing through
    // the Dapper transaction begin; PostgreSQL ships first. MSSQL/Synapse/Snowflake have no
    // READ ONLY transaction mode — those engines keep parser-level enforcement only.
    private static bool SupportsReadOnlyTransaction(DatabaseEngineType engineType)
    {
        return engineType == DatabaseEngineType.PostgreSQL;
    }

    // Engines with an actual dry-run strategy in ValidateQueryAsync's switch: EXPLAIN
    // (PostgreSQL/MySQL/Snowflake) or sp_describe_first_result_set (MSSQL/Synapse). Everything else
    // (SQLite today, any future engine until a strategy is added) reports Skipped.
    private static bool SupportsDryRunValidation(DatabaseEngineType engineType)
    {
        return engineType is DatabaseEngineType.PostgreSQL
            or DatabaseEngineType.MySQL
            or DatabaseEngineType.Snowflake
            or DatabaseEngineType.MSSQL
            or DatabaseEngineType.AzureSynapse;
    }

    private static string ResolveDialect(DatabaseEngineType engineType)
    {
        return engineType switch
        {
            DatabaseEngineType.PostgreSQL => "postgresql",
            DatabaseEngineType.MySQL => "mysql",
            DatabaseEngineType.MSSQL => "sqlserver",
            DatabaseEngineType.AzureSynapse => "azuresynapse",
            DatabaseEngineType.Snowflake => "snowflake",
            _ => ""
        };
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

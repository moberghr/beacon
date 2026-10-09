using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.HostData;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services.Validation;

namespace Beacon.Core.Services.Providers;

internal class DatabaseProvider(
    IDataSourceConnectionResolver connectionResolver,
    SqlReadOnlyAstValidator readOnlyValidator,
    IHostDataSourceGuard hostGuard,
    ILogger<DatabaseProvider> logger) : IDataSourceProvider
{
    /// <summary>What a caller sees when a query against a host data source fails on the server.</summary>
    public const string HostQueryFailedMessage = "Query failed on the host database.";

    /// <summary>What a caller sees when MySQL's read-only transaction could not be closed after a read.</summary>
    public const string MySqlReadOnlyNotClosedMessage = "The read-only transaction could not be closed, so the result was discarded.";

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

            var connectionString = connectionResolver.GetConnectionString(dataSource);
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
                ErrorMessage = ConnectionFailureDescriber.Describe(ex),
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
    // read-only transaction path here (PostgreSQL and MySQL — see SupportsReadOnlyTransaction). For every
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
            var readOnlyError = readOnlyValidator.Validate(query, DataSourceSqlDialect.Of(dataSource));
            if (readOnlyError != null)
            {
                return new QueryValidationResult
                {
                    IsValid = false,
                    Errors = new List<string> { readOnlyError }
                };
            }

            // Host-managed sources (ExposeDbContext): allow-listed tables and exposed columns only.
            var hostCheck = hostGuard.Check(dataSource, query);
            if (!hostCheck.Allowed)
            {
                return new QueryValidationResult
                {
                    IsValid = false,
                    Errors = new List<string> { hostCheck.Error! }
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
            var connectionString = connectionResolver.GetConnectionString(dataSource);
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
            return new QueryValidationResult
            {
                IsValid = false,
                Errors = new List<string> { DescribeFailure(dataSource, ex, "Query validation failed", LogLevel.Warning) }
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

            // Host-managed sources (ExposeDbContext): the policy is enforced HERE as well as in the execution
            // gate, so every caller of the provider — MCP, ad-hoc queries, documentation/value sampling — is covered.
            // A bind parameter the statement uses but the caller did not supply would reach the server as
            // literal text (on PostgreSQL "@p0" is then abs() of a column p0), so it is refused here too.
            var hostCheck = hostGuard.Check(dataSource, query);
            var hostError = hostCheck.Allowed ? hostCheck.FindUnboundParameter(parameters) : hostCheck.Error;
            if (hostError != null)
            {
                stopwatch.Stop();

                return new ProviderQueryResult
                {
                    Rows = new List<Dictionary<string, object?>>(),
                    TotalRows = 0,
                    ExecutionTimeMs = stopwatch.Elapsed.TotalMilliseconds,
                    Success = false,
                    ErrorMessage = hostError
                };
            }

            // §1.5 backstop — parser-level read-only enforcement alone is bypassable (SQL injection past
            // the regex/AST gates), so the database itself rejects writes: any write attempt inside a
            // READ ONLY transaction fails server-side (PostgreSQL 25006, MySQL 1792), regardless of what
            // the parsers missed.
            var engine = dataSource.DatabaseEngineType.Value;
            var useReadOnlyTransaction = enforceReadOnly && SupportsDatabaseReadOnlyEnforcement(engine);
            var usePostgreSqlReadOnlyTransaction = useReadOnlyTransaction && engine == DatabaseEngineType.PostgreSQL;
            var useMySqlReadOnlyTransaction = useReadOnlyTransaction && engine == DatabaseEngineType.MySQL;

            var connectionString = connectionResolver.GetConnectionString(dataSource);
            await using var connection = DbConnectionFactory.CreateConnection(
                engine,
                useMySqlReadOnlyTransaction ? WithoutStatementBatches(connectionString) : connectionString);

            await connection.OpenAsync(cancellationToken);

            if (usePostgreSqlReadOnlyTransaction)
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

            await using var transaction = usePostgreSqlReadOnlyTransaction
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

            List<Dictionary<string, object?>> rows;
            var mySqlReadOnlyClosed = true;
            try
            {
                if (useMySqlReadOnlyTransaction)
                {
                    await BeginMySqlReadOnlyAsync(connection, cancellationToken);
                }

                var result = await connection.QueryAsync(commandDefinition);
                rows = hostGuard.Mask(ConvertDapperResultsToRows(result.AsList()), hostCheck.MaskedOutputColumns);

                if (transaction != null)
                {
                    // Reads inside a READ ONLY transaction commit fine.
                    await transaction.CommitAsync(cancellationToken);
                }
            }
            finally
            {
                if (useMySqlReadOnlyTransaction)
                {
                    mySqlReadOnlyClosed = await EndMySqlReadOnlyAsync(connection, dataSource);
                }
            }

            stopwatch.Stop();

            if (!mySqlReadOnlyClosed)
            {
                // The rows were read, but the connection's read-only state could not be restored; fail closed.
                return new ProviderQueryResult
                {
                    Rows = new List<Dictionary<string, object?>>(),
                    TotalRows = 0,
                    ExecutionTimeMs = stopwatch.Elapsed.TotalMilliseconds,
                    Success = false,
                    ErrorMessage = MySqlReadOnlyNotClosedMessage
                };
            }

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
            stopwatch.Stop();

            return new ProviderQueryResult
            {
                Rows = new List<Dictionary<string, object?>>(),
                TotalRows = 0,
                ExecutionTimeMs = stopwatch.Elapsed.TotalMilliseconds,
                Success = false,
                ErrorMessage = DescribeFailure(dataSource, ex, "Query execution failed", LogLevel.Error)
            };
        }
    }

    // A server or conversion error message can quote row values, so the log never carries the message or the
    // exception: only its type and the driver's error code (§1.11). The caller of an ordinary source still gets the
    // message; a host data source is reached by agents that may be prompt-injected and could quote masked or
    // unexposed columns, so its caller gets the generic text.
    private string DescribeFailure(DataSource dataSource, Exception ex, string what, LogLevel level)
    {
        logger.Log(
            level,
            "{What} for {SourceKind} data source {DataSourceId} with {ExceptionType} (error code {ErrorCode})",
            what,
            dataSource.HostManagedKey == null ? "database" : "host",
            dataSource.Id,
            ex.GetType().Name,
            ErrorCodeOf(ex) ?? "none");

        return dataSource.HostManagedKey == null ? ex.Message : HostQueryFailedMessage;
    }

    // The driver's error code, never text: SQL Server's error number, otherwise the SQLSTATE that the PostgreSQL and
    // MySQL drivers expose through DbException. Looks through wrapping exceptions.
    private static string? ErrorCodeOf(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            switch (current)
            {
                case SqlException sqlServer:
                    return sqlServer.Number.ToString(CultureInfo.InvariantCulture);
                case DbException { SqlState: { Length: > 0 } sqlState }:
                    return sqlState;
            }
        }

        return null;
    }

    // With batches on, MySQL runs every statement of a command, so `COMMIT; <write>` would end the read-only transaction
    // and then write. The read-only connection turns batches off (MySql.Data's AllowBatch); its connection string
    // therefore differs from the ordinary one, so it is pooled apart and only ever runs read-only work.
    private static string WithoutStatementBatches(string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        var batchKeys = builder.Keys
            .Cast<string>()
            .Where(x => x.Replace(" ", "").Equals("allowbatch", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var key in batchKeys)
        {
            builder.Remove(key);
        }

        builder["AllowBatch"] = "false";

        return builder.ConnectionString;
    }

    // Two belts, as on PostgreSQL. The session default makes every transaction on the connection read-only — a DDL
    // statement's implicit commit included, which would otherwise end the explicit transaction and run unchecked — and
    // the explicit transaction is opened READ ONLY. MySQL fixes a transaction's access mode when it starts, so it is
    // opened with SQL rather than through BeginTransaction.
    private static async Task BeginMySqlReadOnlyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "SET SESSION TRANSACTION READ ONLY",
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "START TRANSACTION READ ONLY",
            cancellationToken: cancellationToken));
    }

    // Best effort, on every path after the session went read-only, and never on the caller's token: a request cancelled
    // after a successful read must still close the transaction, and the read stays a success. MySql.Data does not reset
    // session state for a pooled connection, so the session is put back to read-write here. If either statement fails,
    // the driver still rolls back a transaction the server reports open before it pools the connection (it checks the
    // server's in-transaction flag on close), and the connection belongs to the read-only pool anyway.
    private async Task<bool> EndMySqlReadOnlyAsync(DbConnection connection, DataSource dataSource)
    {
        try
        {
            await connection.ExecuteAsync(new CommandDefinition("ROLLBACK", cancellationToken: CancellationToken.None));
            await connection.ExecuteAsync(new CommandDefinition(
                "SET SESSION TRANSACTION READ WRITE",
                cancellationToken: CancellationToken.None));

            return true;
        }
        catch (Exception ex)
        {
            DescribeFailure(dataSource, ex, "Closing the read-only transaction failed", LogLevel.Warning);

            return false;
        }
    }

    // PostgreSQL supports the session-level default_transaction_read_only backstop plus
    // SET TRANSACTION READ ONLY as the first statement of an open transaction. MySQL (5.6.5+, MariaDB
    // 10.0+) gets the session-level SET SESSION TRANSACTION READ ONLY plus START TRANSACTION READ ONLY
    // on a connection without statement batches. MSSQL/Synapse/Snowflake have no READ ONLY transaction
    // mode — those engines keep parser-level enforcement only.
    private static bool SupportsReadOnlyTransaction(DatabaseEngineType engineType)
    {
        return engineType is DatabaseEngineType.PostgreSQL or DatabaseEngineType.MySQL;
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

using System.Data.Common;
using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.HostData;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services.Validation;
using DatabaseErrorRedactor = Beacon.Core.Services.Security.DatabaseErrorRedactor;

namespace Beacon.Core.Services.Providers;

internal class DatabaseProvider(
    IDataSourceConnectionResolver connectionResolver,
    SqlReadOnlyAstValidator readOnlyValidator,
    IHostDataSourceGuard hostGuard,
    IConfiguredDatabaseResolver configuredDatabases,
    ILogger<DatabaseProvider> logger,
    IOptions<QueryExecutionOptions> queryOptions) : IDataSourceProvider, IRowCappedReadOnlyExecution
{
    /// <summary>What a caller sees when a query against a host data source fails on the server.</summary>
    public const string HostQueryFailedMessage = "Query failed on the host database.";

    /// <summary>
    /// What a caller sees when a host data source's result did not have the columns the host policy checked, so its
    /// masking could not be placed and the result was discarded.
    /// </summary>
    public const string HostResultMismatchMessage =
        "The result's columns did not line up with the statement's columns as the host data source policy checked them, so the result was discarded. Rewrite the query so each output column is a plain column or has a short alias of its own, with no duplicate names.";

    /// <summary>What a caller sees when MySQL's read-only transaction could not be closed after a read.</summary>
    public const string MySqlReadOnlyNotClosedMessage = "The read-only transaction could not be closed, so the result was discarded.";

    /// <summary>What a caller sees when a SQL Server read ended the transaction Beacon runs it in.</summary>
    public const string SqlServerTransactionEndedMessage =
        "The query ended the transaction it runs in, so its result was discarded. Only a single SELECT statement may run.";

    /// <summary>
    /// What a caller sees when a read stopped at the row cap could not close MySQL's read-only transaction: the result had
    /// more rows than <paramref name="ceiling"/> (the provider reads one row past the caller's ceiling).
    /// </summary>
    internal static string MoreRowsThanMessage(int ceiling) =>
        $"The query returned more than {ceiling} rows, so its read was stopped and the result discarded; filter or aggregate it so the result fits.";

    // How long one statement may run on the server (Beacon:Query:StatementTimeoutSeconds).
    private int StatementTimeoutSeconds { get; } = queryOptions.Value.StatementTimeoutSeconds;

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
        return ExecuteQueryCoreAsync(dataSource, query, parameters, enforceReadOnly: false, maxRows: null, cancellationToken);
    }

    public Task<ProviderQueryResult> ExecuteReadOnlyQueryAsync(
        DataSource dataSource,
        string query,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        return ExecuteQueryCoreAsync(dataSource, query, parameters, enforceReadOnly: true, maxRows: null, cancellationToken);
    }

    public Task<ProviderQueryResult> ExecuteReadOnlyQueryAsync(
        DataSource dataSource,
        string query,
        Dictionary<string, object?> parameters,
        int maxRows,
        CancellationToken cancellationToken)
    {
        return ExecuteQueryCoreAsync(dataSource, query, parameters, enforceReadOnly: true, maxRows, cancellationToken);
    }

    // Honest capability report: the database-level backstop exists ONLY for engines with a working
    // read-only transaction path here (PostgreSQL and MySQL — see SupportsReadOnlyTransaction). SQL Server
    // runs the read-only path in a transaction that is always rolled back, which undoes a write but cannot
    // refuse one, so it is not reported as enforcement; every other engine runs plain and relies on the
    // parser gates and a read-only login.
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
            var dialect = DataSourceSqlDialect.Of(dataSource);
            var readOnlyError = readOnlyValidator.Validate(query, dialect);
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

            // Names in the database the source's connection is configured for only.
            var crossDatabaseError = SqlCrossDatabaseRule.Validate(query, dialect, configuredDatabases.Resolve(dataSource));
            if (crossDatabaseError != null)
            {
                return new QueryValidationResult
                {
                    IsValid = false,
                    Errors = new List<string> { crossDatabaseError }
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

            // Basic validation: try to prepare the query without executing. The EXPLAIN carries the caller's text, so on
            // MySQL it goes over a connection without statement batches, and on PostgreSQL as a single-statement batch:
            // the driver never runs a second statement hidden in it.
            var engine = dataSource.DatabaseEngineType.Value;
            var connectionString = connectionResolver.GetConnectionString(dataSource);
            await using var connection = DbConnectionFactory.CreateConnection(
                engine,
                engine == DatabaseEngineType.MySQL ? WithoutStatementBatches(connectionString) : connectionString);

            await connection.OpenAsync(cancellationToken);

            // Engine-specific dry-run: validates syntax and column binding without executing the query.
            switch (dataSource.DatabaseEngineType)
            {
                case DatabaseEngineType.PostgreSQL:
                    await ReadSingleStatementAsync(connection, $"EXPLAIN {query}", new Dictionary<string, object?>(), null, null, 30, cancellationToken);
                    break;

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
        int? maxRows,
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

            // SQL Server has no read-only transaction or session mode. Its read-only path runs inside a transaction that
            // is always rolled back, so a write the parsers missed in the one statement they admit is undone rather than
            // committed. It cannot refuse the write (only a read-only login can), so SupportsDatabaseReadOnlyEnforcement
            // stays false.
            var useSqlServerRollbackOnlyTransaction = enforceReadOnly && engine == DatabaseEngineType.MSSQL;

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

            await using var transaction = usePostgreSqlReadOnlyTransaction || useSqlServerRollbackOnlyTransaction
                ? await connection.BeginTransactionAsync(cancellationToken)
                : null;

            if (usePostgreSqlReadOnlyTransaction)
            {
                // Inner belt: the explicit transaction is additionally opened READ ONLY.
                await connection.ExecuteAsync(new CommandDefinition(
                    "SET TRANSACTION READ ONLY",
                    transaction: transaction,
                    cancellationToken: cancellationToken));
            }

            // A savepoint under a name nobody else knows: the transaction is still Beacon's own after the read only if the
            // savepoint can still be rolled back to.
            var savepoint = useSqlServerRollbackOnlyTransaction ? $"r{Guid.NewGuid():N}"[..32] : null;
            if (savepoint != null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "SAVE TRANSACTION @savepoint",
                    new { savepoint },
                    transaction,
                    cancellationToken: cancellationToken));
            }

            var commandDefinition = new CommandDefinition(
                query,
                parameters,
                transaction: transaction,
                cancellationToken: cancellationToken,
                commandTimeout: StatementTimeoutSeconds);

            List<Dictionary<string, object?>> rows;
            HostMaskOutcome masking;
            var mySqlReadOnlyClosed = true;
            var capped = false;
            try
            {
                if (useMySqlReadOnlyTransaction)
                {
                    await BeginMySqlReadOnlyAsync(connection, cancellationToken);
                }

                // On PostgreSQL the statement goes out as a batch of exactly one command, so the driver refuses a second
                // statement (a COMMIT, a BEGIN READ WRITE) rather than splitting the text and running each — the READ ONLY
                // session and transaction alone cannot stop a statement that ends the transaction and opens a writable one.
                var read = usePostgreSqlReadOnlyTransaction
                    ? await ReadSingleStatementAsync(connection, query, parameters, transaction, maxRows, StatementTimeoutSeconds, cancellationToken)
                    : await ReadRowsAsync(connection, commandDefinition, maxRows, cancellationToken);
                capped = read.Capped;

                // A host data source's masked columns are masked by their POSITION in the result, before the rows are keyed
                // by column name — the name the database gives a column is the caller's to choose (an alias), and a
                // repeated name would hide a position.
                masking = HostResultMasker.Mask(hostCheck, dataSource.HostManagedKey != null, read.Columns, read.Rows);
                rows = masking.Discarded ? [] : ToRows(read.Columns, read.Rows);

                if (useSqlServerRollbackOnlyTransaction)
                {
                    var intact = await SqlServerTransactionIntactAsync(connection, transaction!, savepoint!);
                    if (!intact)
                    {
                        // The statement ended Beacon's transaction (a COMMIT or ROLLBACK in the batch), so whatever it did
                        // after that was not undone. The rows are discarded and the run fails.
                        logger.LogWarning(
                            ProjectAccessEvents.ReadOnlyTransactionEnded,
                            "A read-only query on data source {DataSourceId} ended its rolled-back transaction; its result was discarded",
                            dataSource.Id);

                        return Failed(stopwatch, SqlServerTransactionEndedMessage);
                    }

                    await transaction!.RollbackAsync(CancellationToken.None);
                }
                else if (transaction != null && !capped)
                {
                    // Reads inside a READ ONLY transaction commit fine. A read stopped at the row cap cancelled the rest
                    // of its statement, so its transaction is rolled back on disposal instead.
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

            if (masking.Discarded)
            {
                logger.LogWarning(
                    "A result from host data source {DataSourceId} had {ActualColumns} columns where the host policy checked {ExpectedColumns}; it was discarded",
                    dataSource.Id,
                    masking.ActualColumns,
                    masking.ExpectedColumns);

                return Failed(stopwatch, HostResultMismatchMessage);
            }

            if (masking.RenamedColumns > 0)
            {
                logger.LogWarning(
                    "{RenamedColumns} of {ColumnCount} result columns from host data source {DataSourceId} were not named as the host policy derived them and were masked",
                    masking.RenamedColumns,
                    masking.ActualColumns,
                    dataSource.Id);
            }

            if (!mySqlReadOnlyClosed)
            {
                // The rows were read, but the connection's read-only state could not be restored; fail closed. A read stopped
                // at the row cap cancelled the rest of its statement, which can leave the transaction unable to close: that
                // result was over the cap anyway, and the cap is what the caller has to act on.
                return Failed(stopwatch, capped && maxRows is { } readLimit ? MoreRowsThanMessage(readLimit - 1) : MySqlReadOnlyNotClosedMessage);
            }

            var metadata = new Dictionary<string, object?>
            {
                ["DatabaseEngine"] = dataSource.DatabaseEngineType.Value.ToString()
            };
            if (masking.RenamedColumns > 0)
            {
                metadata[HostResultMetadata.RenamedMaskedColumns] = masking.RenamedColumns;
            }

            return new ProviderQueryResult
            {
                Rows = rows,
                TotalRows = rows.Count,
                ExecutionTimeMs = stopwatch.Elapsed.TotalMilliseconds,
                Success = true,
                Metadata = metadata
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

    // Without a cap, the whole result is buffered. With one, at most maxRows rows are read: Dapper cancels the command of a
    // reader it closes early, so a statement whose own LIMIT / TOP lies above the cap is not fetched in full. The driver
    // may report that cancellation while the reader closes; the rows were read by then, and the caller judges a result
    // that reached the cap as too large.
    private static async Task<ReadResult> ReadRowsAsync(
        DbConnection connection,
        CommandDefinition command,
        int? maxRows,
        CancellationToken cancellationToken)
    {
        if (maxRows is not { } cap)
        {
            return FromDapperRows((await connection.QueryAsync(command)).AsList(), false);
        }

        var rows = new List<dynamic>();
        var reader = connection
            .QueryUnbufferedAsync(command.CommandText, command.Parameters, (DbTransaction?)command.Transaction, command.CommandTimeout)
            .WithCancellation(cancellationToken)
            .GetAsyncEnumerator();
        try
        {
            while (rows.Count < cap && await reader.MoveNextAsync())
            {
                rows.Add(reader.Current);
            }
        }
        finally
        {
            try
            {
                await reader.DisposeAsync();
            }
            catch (Exception) when (rows.Count >= cap)
            {
                // The statement was cancelled after the cap was reached; its rows are already read.
            }
        }

        return FromDapperRows(rows, rows.Count >= cap);
    }

    /// <summary>
    /// Runs <paramref name="statement"/> as a batch holding exactly one command, reading at most <paramref name="maxRows"/>
    /// rows as <see cref="ReadRowsAsync"/> does. A batch command is never split into statements: the PostgreSQL driver
    /// refuses a command text that holds a second statement, and text it does not split reaches the server as one
    /// extended-protocol Parse message, which PostgreSQL refuses when it holds more than one statement — so neither
    /// verdict rests on Beacon's own parser. Parameters bind by name, a null as SQL NULL.
    /// </summary>
    private static async Task<ReadResult> ReadSingleStatementAsync(
        DbConnection connection,
        string statement,
        IReadOnlyDictionary<string, object?> parameters,
        DbTransaction? transaction,
        int? maxRows,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (!connection.CanCreateBatch)
        {
            throw new BeaconException("The database driver cannot run a single-statement batch, so the read-only query was not run.");
        }

        await using var batch = connection.CreateBatch();
        batch.Transaction = transaction;
        batch.Timeout = timeoutSeconds;
        var command = batch.CreateBatchCommand();
        command.CommandText = statement;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        batch.BatchCommands.Add(command);

        var rows = new List<object?[]>();
        var cap = maxRows ?? int.MaxValue;
        var reader = await batch.ExecuteReaderAsync(cancellationToken);
        var columns = new List<string>();
        try
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                columns.Add(reader.GetName(i));
            }

            while (rows.Count < cap && await reader.ReadAsync(cancellationToken))
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                rows.Add(row);
            }

            if (rows.Count >= cap)
            {
                // Stop the rest of the statement instead of letting the reader drain it on close.
                batch.Cancel();
            }
        }
        finally
        {
            try
            {
                await reader.DisposeAsync();
            }
            catch (Exception) when (rows.Count >= cap)
            {
                // The statement was cancelled after the cap was reached; its rows are already read.
            }
        }

        return new ReadResult(columns, rows, maxRows != null && rows.Count >= cap);
    }

    // Beacon's transaction is intact when it is still open and the savepoint set before the read can still be rolled back
    // to: a COMMIT or ROLLBACK in the batch removes the savepoint, also when the batch opens a new transaction after it.
    // Any failure to tell counts as ended.
    private static async Task<bool> SqlServerTransactionIntactAsync(DbConnection connection, DbTransaction transaction, string savepoint)
    {
        try
        {
            if (transaction.Connection == null)
            {
                return false;
            }

            var open = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT @@TRANCOUNT",
                transaction: transaction,
                cancellationToken: CancellationToken.None));
            if (open < 1)
            {
                return false;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "ROLLBACK TRANSACTION @savepoint",
                new { savepoint },
                transaction,
                cancellationToken: CancellationToken.None));

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static ProviderQueryResult Failed(Stopwatch stopwatch, string message)
    {
        stopwatch.Stop();

        return new ProviderQueryResult
        {
            Rows = new List<Dictionary<string, object?>>(),
            TotalRows = 0,
            ExecutionTimeMs = stopwatch.Elapsed.TotalMilliseconds,
            Success = false,
            ErrorMessage = message
        };
    }

    // A server or conversion error message can quote row values, so the log never carries the message or the
    // exception: only its type and the driver's error code (§1.11). The caller of an ordinary source still gets the
    // message, led by its code when the statement's own code raised it (DatabaseErrorRedactor.TextOf); a host data source
    // is reached by agents that may be prompt-injected and could quote masked or unexposed columns, so its caller gets the
    // generic text.
    private string DescribeFailure(DataSource dataSource, Exception ex, string what, LogLevel level)
    {
        logger.Log(
            level,
            "{What} for {SourceKind} data source {DataSourceId} with {Failure}",
            what,
            dataSource.HostManagedKey == null ? "database" : "host",
            dataSource.Id,
            FailureCodes.Describe(ex));

        return dataSource.HostManagedKey == null ? DatabaseErrorRedactor.TextOf(ex) : HostQueryFailedMessage;
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
    // mode — SQL Server gets a rollback-only transaction, Synapse and Snowflake run plain; all three rely
    // on the parser gates and a read-only login.
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

    // A Dapper row enumerates every field of its reader in reader order — a repeated name repeated — so field i of the
    // enumeration is result column i.
    private static ReadResult FromDapperRows(List<dynamic> dapperRows, bool capped)
    {
        var columns = new List<string>();
        var rows = new List<object?[]>();

        foreach (var row in dapperRows)
        {
            var fields = row is IDictionary<string, object> dictionary
                ? dictionary.ToList()
                : new List<KeyValuePair<string, object>>();

            if (rows.Count == 0)
            {
                columns = fields
                    .Select(x => x.Key)
                    .ToList();
            }

            rows.Add(fields
                .Select(x => (object?)x.Value)
                .ToArray());
        }

        return new ReadResult(columns, rows, capped);
    }

    // Keyed by column name the way callers read rows; a repeated name keeps its last column's value.
    private static List<Dictionary<string, object?>> ToRows(IReadOnlyList<string> columns, List<object?[]> rows)
    {
        var result = new List<Dictionary<string, object?>>(rows.Count);

        foreach (var values in rows)
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < values.Length && i < columns.Count; i++)
            {
                row[columns[i]] = values[i];
            }

            result.Add(row);
        }

        return result;
    }

    /// <summary>A result as read: its column names and each row's values, both in reader order.</summary>
    private sealed record ReadResult(IReadOnlyList<string> Columns, List<object?[]> Rows, bool Capped);
}

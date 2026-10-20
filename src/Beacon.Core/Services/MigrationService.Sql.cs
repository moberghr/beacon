using System.Data.Common;
using System.Dynamic;
using Dapper;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.DataMigration;
using Beacon.Core.Helpers;
using Beacon.Core.Models;

namespace Beacon.Core.Services;

internal partial class MigrationService
{
    /// <summary>
    /// The writer's connection to <paramref name="dataSource"/>, the one place Beacon decrypts a migration write target's
    /// credentials: refused, before they are decrypted, for a source that is not a write target, or is host-managed or marked
    /// read-only. Internal for unit tests.
    /// </summary>
    internal DbConnection CreateDatabaseConnection(Data.Entities.DataSource dataSource)
    {
        try
        {
            if (!dataSource.DatabaseEngineType.HasValue)
                throw new BeaconException($"Data source {dataSource.Id} is not a database type");

            // Host-managed sources are read-only, policy-gated views of the host database; bulk copy bypasses that.
            if (dataSource.HostManagedKey != null)
                throw new InvalidOperationException($"Host data source '{dataSource.Name}' cannot be used by migration jobs.");

            // The writer connects only to a migration write target: the last check before its credentials are decrypted.
            var refusal = MigrationWriteTargets.DestinationRefusal(dataSource);
            if (refusal != null)
            {
                throw new InvalidOperationException(refusal);
            }

            var connectionString = connectionResolver.GetWriteConnectionString(dataSource);
            return DbConnectionFactory.CreateConnection(dataSource.DatabaseEngineType.Value, connectionString);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create database connection for data source '{dataSource.Name}': {ex.Message}", ex);
        }
    }

    private async Task ValidateDestinationTable(DbConnection connection, string? schema, string table, DatabaseEngineType engineType)
    {
        var tableName = schema == null ? table : $"{schema}.{table}";
        try
        {
            // Identifiers appear as string literals here, but are still whitelist-validated (§1.10);
            // embedded single quotes are doubled defence-in-depth.
            if (schema != null)
            {
                SqlIdentifierGuard.Validate(schema, "schema");
            }
            SqlIdentifierGuard.Validate(table, "table");
            var schemaLiteral = schema?.Replace("'", "''");
            var tableLiteral = table.Replace("'", "''");

            var checkQuery = engineType switch
            {
                DatabaseEngineType.PostgreSQL => schema != null
                    ? $"SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_schema = '{schemaLiteral}' AND table_name = '{tableLiteral}')"
                    : $"SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_name = '{tableLiteral}')",
                DatabaseEngineType.MySQL => schema != null
                    ? $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = '{schemaLiteral}' AND table_name = '{tableLiteral}'"
                    : $"SELECT COUNT(*) FROM information_schema.tables WHERE table_name = '{tableLiteral}' AND table_schema = DATABASE()",
                DatabaseEngineType.MSSQL => schema != null
                    ? $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{schemaLiteral}' AND TABLE_NAME = '{tableLiteral}'"
                    : $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = '{tableLiteral}'",
                _ => throw new NotSupportedException($"Database engine type '{engineType}' is not supported")
            };

            var exists = await connection.ExecuteScalarAsync<bool>(checkQuery);
            if (!exists)
            {
                throw new InvalidOperationException($"Table '{tableName}' does not exist in the destination database");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Failed to validate destination table '{tableName}': {ex.Message}", ex);
        }
    }

    private (string? schema, string table) ParseSchemaAndTableName(string tableName)
    {
        // Handle schema-qualified table names (e.g., "schema.table")
        var parts = tableName.Split('.', 2);
        if (parts.Length == 2)
        {
            return (parts[0], parts[1]);
        }
        return (null, tableName);
    }

    private async Task ExecuteTruncate(DbConnection connection, DbTransaction transaction, string tableName, DatabaseEngineType engineType)
    {
        try
        {
            var truncateQuery = $"TRUNCATE TABLE {ValidateTable(tableName)}";
            await connection.ExecuteAsync(truncateQuery, transaction: transaction);
            logger.LogInformation("Truncated table {Table}", tableName);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to truncate table '{tableName}': {ex.Message}", ex);
        }
    }

    private async Task<(int rowsWritten, int rowsFailed, List<string> errorDetails)> ExecuteInserts(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        List<Dictionary<string, object?>> data,
        DatabaseEngineType engineType)
    {
        var errorDetails = new List<string>();

        if (!data.Any())
            return (0, 0, errorDetails);

        return await ExecuteGenericBulkInsert(connection, transaction, tableName, data, engineType, errorDetails);
    }

    private async Task<(int rowsWritten, int rowsFailed, List<string> errorDetails)> ExecuteUpserts(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        List<Dictionary<string, object?>> data,
        IReadOnlyList<string> primaryKeyColumns,
        DatabaseEngineType engineType)
    {
        var errorDetails = new List<string>();

        if (!data.Any())
            return (0, 0, errorDetails);

        try
        {
            // Temp table + merge (MySQL)
            var tempTableName = $"temp_{Guid.NewGuid():N}";

            // Filter out any empty column names
            var columns = data.First().Keys.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();

            if (!columns.Any())
            {
                throw new InvalidOperationException("Source data contains no valid column names");
            }

            try
            {
                // Step 1: Create temp table with same structure as destination
                logger.LogDebug("Creating temp table {TempTable} from {SourceTable}", tempTableName, tableName);
                await CreateTempTable(connection, transaction, tempTableName, tableName, engineType);
                logger.LogDebug("Temp table created successfully");

                // Step 2: Bulk insert data into temp table using PhenX
                // Filter data to only include valid columns
                var filteredData = data.Select(row =>
                    row.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key))
                       .ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                ).ToList();

                await using var bulkContext = CreateBulkContextWithConnection(connection, transaction, engineType, tempTableName, null);
                var entities = ConvertToExpandoObjects(filteredData);

                logger.LogDebug("Bulk inserting {RowCount} rows into temp table {TempTable}", entities.Count, tempTableName);

                var bulkConfig = new BulkConfig
                {
                    SetOutputIdentity = false,
                    BulkCopyTimeout = Constants.Migration.BulkCopyTimeoutSeconds,
                    BatchSize = Constants.Migration.UpsertBatchSize,
                    CustomDestinationTableName = tempTableName,
                    UseTempDB = false
                };

                await bulkContext.BulkInsertAsync(entities.Cast<object>().ToList(), bulkConfig);

                // Step 3: Merge from temp table to destination table
                var mergeQuery = BuildMergeQuery(tableName, tempTableName, columns, primaryKeyColumns.ToList(), engineType);

                logger.LogDebug("Executing merge query: {MergeQuery}", mergeQuery);

                try
                {
                    await connection.ExecuteAsync(mergeQuery, transaction: transaction);
                }
                catch (Exception mergeEx)
                {
                    // §1.11 — log identifiers and the failure's type and code, not the generated SQL body or the message.
                    logger.LogError("Merge query failed for table {Table} ({Failure})", tableName, FailureCodes.Describe(mergeEx));
                    throw new InvalidOperationException($"Merge query failed for table '{tableName}': {mergeEx.Message}", mergeEx);
                }

                logger.LogInformation("Bulk upserted {RowCount} rows into {Table} via temp table", data.Count, tableName);
                return (data.Count, 0, errorDetails);
            }
            finally
            {
                // Clean up temp table
                try
                {
                    await connection.ExecuteAsync($"DROP TABLE IF EXISTS {SqlIdentifierGuard.Validate(tempTableName, "temp table")}", transaction: transaction);
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Failed to drop temp table {TempTable}: {Failure}", tempTableName, FailureCodes.Describe(ex));
                }
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Upsert operation failed: {ex.Message}", ex);
        }
    }

    private async Task<(int rowsWritten, int rowsFailed, List<string> errorDetails)> ExecuteGenericBulkInsert(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        List<Dictionary<string, object?>> data,
        DatabaseEngineType engineType,
        List<string> errorDetails)
    {
        try
        {
            // Use EFCore.BulkExtensions for other databases
            await using var bulkContext = CreateBulkContextWithConnection(connection, transaction, engineType, tableName, null);

            var entities = ConvertToExpandoObjects(data);

            var bulkConfig = new BulkConfig
            {
                SetOutputIdentity = false,
                BulkCopyTimeout = Constants.Migration.BulkCopyTimeoutSeconds,
                BatchSize = Constants.Migration.BulkInsertBatchSize,
                CustomDestinationTableName = tableName,
                UseTempDB = false
            };

            await bulkContext.BulkInsertAsync(entities.Cast<object>().ToList(), bulkConfig);

            logger.LogInformation("Bulk inserted {RowCount} rows into {Table}", data.Count, tableName);
            return (data.Count, 0, errorDetails);
        }
        catch (Exception ex)
        {
            logger.LogError("Bulk insert failed for table {Table}, falling back to row-by-row ({Failure})", tableName, FailureCodes.Describe(ex));

            // Fallback to row-by-row insertion on bulk failure
            var rowsWritten = 0;
            var rowsFailed = 0;

            foreach (var row in data)
            {
                try
                {
                    foreach (var key in row.Keys)
                    {
                        SqlIdentifierGuard.Validate(key, "column");
                    }
                    var columns = string.Join(", ", row.Keys);
                    var parameters = string.Join(", ", row.Keys.Select(x => $"@{x}"));
                    var insertQuery = $"INSERT INTO {ValidateTable(tableName)} ({columns}) VALUES ({parameters})";

                    await connection.ExecuteAsync(insertQuery, row, transaction);
                    rowsWritten++;
                }
                catch (Exception rowEx)
                {
                    rowsFailed++;
                    var errorMsg = $"Row {rowsWritten + rowsFailed} failed: {rowEx.Message}";
                    errorDetails.Add(errorMsg);
                    logger.LogWarning("Failed to insert row into {Table}: {Failure}", tableName, FailureCodes.Describe(rowEx));

                    if (rowsFailed > Constants.Migration.MaxFailedRowsBeforeStop)
                    {
                        errorDetails.Add($"Too many errors (>{Constants.Migration.MaxFailedRowsBeforeStop}), stopping insertion");
                        break;
                    }
                }
            }

            return (rowsWritten, rowsFailed, errorDetails);
        }
    }

    private DynamicDbContext CreateBulkContextWithConnection(DbConnection connection, DbTransaction transaction, DatabaseEngineType engineType, string? tableName = null, List<string>? primaryKeys = null)
    {
        return bulkContextFactory.Create(connection, transaction, engineType, tableName, primaryKeys);
    }

    private List<ExpandoObject> ConvertToExpandoObjects(List<Dictionary<string, object?>> data)
    {
        var result = new List<ExpandoObject>();

        foreach (var row in data)
        {
            var expando = new ExpandoObject();
            var expandoDict = (IDictionary<string, object?>)expando;

            foreach (var kvp in row)
            {
                expandoDict[kvp.Key] = kvp.Value;
            }

            result.Add(expando);
        }

        return result;
    }

    private async Task CreateTempTable(DbConnection connection, DbTransaction transaction, string tempTableName, string sourceTableName, DatabaseEngineType engineType)
    {
        SqlIdentifierGuard.Validate(tempTableName, "temp table");
        var validatedSource = ValidateTable(sourceTableName);

        var createQuery = engineType switch
        {
            DatabaseEngineType.MySQL => $"CREATE TEMPORARY TABLE {tempTableName} LIKE {validatedSource}",
            _ => throw new NotSupportedException($"Database engine type '{engineType}' is not supported")
        };

        try
        {
            await connection.ExecuteAsync(createQuery, transaction: transaction);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create temp table with query: {createQuery}. Error: {ex.Message}", ex);
        }
    }

    private string BuildMergeQuery(string destinationTable, string sourceTable, List<string> columns, List<string> primaryKeyColumns, DatabaseEngineType engineType)
    {
        var nonKeyColumns = columns.Except(primaryKeyColumns).ToList();

        return engineType switch
        {
            DatabaseEngineType.MySQL => BuildMySqlMerge(destinationTable, sourceTable, columns, primaryKeyColumns, nonKeyColumns),
            _ => throw new NotSupportedException($"Database engine type '{engineType}' is not supported")
        };
    }

    private string BuildMySqlMerge(string destinationTable, string sourceTable, List<string> columns, List<string> primaryKeyColumns, List<string> nonKeyColumns)
    {
        foreach (var x in columns)
        {
            SqlIdentifierGuard.Validate(x, "column");
        }
        var validatedDestination = ValidateTable(destinationTable);
        var validatedSource = ValidateTable(sourceTable);

        var columnList = string.Join(", ", columns);
        var sourceColumns = string.Join(", ", columns.Select(x => $"s.{x}"));
        var updateSet = nonKeyColumns.Any()
            ? string.Join(", ", nonKeyColumns.Select(x => $"{x} = VALUES({x})"))
            : primaryKeyColumns.First() + " = " + primaryKeyColumns.First(); // Dummy update

        return $@"
            INSERT INTO {validatedDestination} ({columnList})
            SELECT {columnList} FROM {validatedSource}
            ON DUPLICATE KEY UPDATE {updateSet}";
    }

    /// <summary>
    /// The rows to write, with every column name validated (§1.10): the names of all rows in the order they first appear,
    /// empty names left out. A row without one of the columns writes NULL there.
    /// </summary>
    private static MigrationRows MigrationRowsOf(List<Dictionary<string, object?>> data)
    {
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in data)
        {
            foreach (var column in row.Keys)
            {
                if (string.IsNullOrWhiteSpace(column) || !seen.Add(column))
                {
                    continue;
                }

                SqlIdentifierGuard.Validate(column, "column");
                columns.Add(column);
            }
        }

        if (data.Count > 0 && columns.Count == 0)
        {
            throw new InvalidOperationException("Source data contains no valid column names");
        }

        return new MigrationRows(columns, data);
    }

    /// <summary>
    /// Writes <paramref name="rows"/> through the engine's writer, inside the run's transaction: Truncate clears the table
    /// first; rows go in chunks of <see cref="Constants.Migration.BulkInsertBatchSize"/>, never an empty one.
    /// </summary>
    private static async Task<int> WriteThroughAsync(
        IMigrationBulkWriter writer,
        MigrationWriteTarget target,
        MigrationRows rows,
        MigrationMode mode,
        IReadOnlyList<string> keyColumns,
        CancellationToken cancellationToken)
    {
        if (mode == MigrationMode.Truncate)
        {
            await writer.ClearAsync(target, cancellationToken);
        }

        var rowsWritten = 0;
        foreach (var chunk in rows.Rows.Chunk(Constants.Migration.BulkInsertBatchSize))
        {
            var chunkRows = new MigrationRows(rows.Columns, chunk);
            rowsWritten += mode == MigrationMode.Upsert
                ? await writer.UpsertAsync(target, chunkRows, keyColumns, cancellationToken)
                : await writer.InsertAsync(target, chunkRows, cancellationToken);
        }

        return rowsWritten;
    }

    /// <summary>
    /// The destination table's primary key, which Upsert matches rows by. A table without one, a key column that is not a
    /// plain identifier, or a row lacking a key column fails the run.
    /// </summary>
    private async Task<IReadOnlyList<string>> RequirePrimaryKey(
        DbConnection connection,
        DbTransaction transaction,
        string? schema,
        string table,
        MigrationRows rows,
        DatabaseEngineType engineType,
        CancellationToken cancellationToken)
    {
        var keyColumns = await GetPrimaryKeyColumns(connection, transaction, schema, table, engineType, cancellationToken);
        var tableName = schema == null ? table : $"{schema}.{table}";
        if (keyColumns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Table '{tableName}' has no primary key. Upsert mode matches rows by the primary key: add one to the table, or run the job in Insert or Truncate mode.");
        }

        foreach (var keyColumn in keyColumns)
        {
            SqlIdentifierGuard.Validate(keyColumn, "key column");
        }

        if (rows.Rows.Any(x => !keyColumns.All(x.ContainsKey)))
        {
            throw new InvalidOperationException($"Some rows are missing primary key columns: {string.Join(", ", keyColumns)}");
        }

        return keyColumns;
    }

    /// <summary>The primary key columns of the table, in key order, read inside <paramref name="transaction"/>; a failed lookup throws.</summary>
    private async Task<List<string>> GetPrimaryKeyColumns(
        DbConnection connection,
        DbTransaction transaction,
        string? schema,
        string table,
        DatabaseEngineType engineType,
        CancellationToken cancellationToken)
    {
        var query = engineType switch
        {
            DatabaseEngineType.PostgreSQL => @"SELECT a.attname
                    FROM pg_index i
                    JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey)
                    WHERE i.indrelid = @tableName::regclass AND i.indisprimary
                    ORDER BY array_position(i.indkey, a.attnum)",

            DatabaseEngineType.MySQL => schema != null
                ? @"SELECT COLUMN_NAME
                    FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
                    WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table AND CONSTRAINT_NAME = 'PRIMARY'
                    ORDER BY ORDINAL_POSITION"
                : @"SELECT COLUMN_NAME
                    FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
                    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND CONSTRAINT_NAME = 'PRIMARY'
                    ORDER BY ORDINAL_POSITION",

            DatabaseEngineType.MSSQL => @"SELECT c.name
                    FROM sys.indexes i
                    INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                    INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                    WHERE i.object_id = OBJECT_ID(@tableName) AND i.is_primary_key = 1
                    ORDER BY ic.key_ordinal",

            _ => throw new NotSupportedException($"Database engine type '{engineType}' is not supported")
        };

        // PostgreSQL (regclass) and SQL Server (OBJECT_ID) resolve the table from its quoted name, the way the writer names it,
        // so the key is read from the very table the rows go into.
        object parameters = engineType == DatabaseEngineType.MySQL
            ? new { schema, table }
            : new { tableName = QuotedTableName(schema, table, engineType) };

        try
        {
            var command = new CommandDefinition(query, parameters, transaction, cancellationToken: cancellationToken);
            var result = await connection.QueryAsync<string>(command);
            return result.ToList();
        }
        catch (Exception ex)
        {
            var tableName = schema == null ? table : $"{schema}.{table}";
            logger.LogError("Failed to read the primary key of table {Table} ({Failure})", SqlIdentifierGuard.ForLog(tableName), FailureCodes.Describe(ex));
            throw new InvalidOperationException($"Failed to read the primary key of table '{tableName}': {ex.Message}", ex);
        }
    }

    // §1.10 — Identifiers (schema / table / column names) cannot be parameterized, so every
    // identifier interpolated into SQL text below is whitelist-validated via SqlIdentifierGuard
    // before it reaches a query string. Where an identifier is wrapped in quotes, the embedded
    // quote char is doubled as defence-in-depth.

    private static string QuotedTableName(string? schema, string table, DatabaseEngineType engineType)
    {
        var dialect = engineType.ToString();
        var quotedTable = SqlIdentifierGuard.Quote(SqlIdentifierGuard.Validate(table, "table"), dialect);

        return schema == null
            ? quotedTable
            : $"{SqlIdentifierGuard.Quote(SqlIdentifierGuard.Validate(schema, "schema"), dialect)}.{quotedTable}";
    }

    private static string ValidateTable(string tableName)
    {
        var parts = tableName.Split('.', 2);
        if (parts.Length == 2)
        {
            SqlIdentifierGuard.Validate(parts[0], "schema");
            SqlIdentifierGuard.Validate(parts[1], "table");
            return tableName;
        }

        return SqlIdentifierGuard.Validate(tableName, "table");
    }

    private static string GetFullExceptionMessage(Exception ex)
    {
        var messages = new List<string>();
        var currentException = ex;

        while (currentException != null)
        {
            messages.Add(currentException.Message);
            currentException = currentException.InnerException;
        }

        return string.Join(" --> ", messages);
    }
}

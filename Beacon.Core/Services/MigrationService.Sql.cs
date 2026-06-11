using System.Data.Common;
using System.Dynamic;
using Dapper;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Helpers.BulkHelpers;
using Beacon.Core.Models;

namespace Beacon.Core.Services;

internal partial class MigrationService
{
    private DbConnection CreateDatabaseConnection(Data.Entities.DataSource dataSource)
    {
        try
        {
            if (!dataSource.DatabaseEngineType.HasValue)
                throw new BeaconException($"Data source {dataSource.Id} is not a database type");

            var connectionString = encryptionService.Decrypt(dataSource.EncryptedConnectionData);
            return DbConnectionFactory.CreateConnection(dataSource.DatabaseEngineType.Value, connectionString);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create database connection for data source '{dataSource.Name}': {ex.Message}", ex);
        }
    }

    private async Task ValidateDestinationTable(DbConnection connection, string tableName, DatabaseEngineType engineType)
    {
        try
        {
            // Parse schema and table name
            var (schema, table) = ParseSchemaAndTableName(tableName);

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

        // Use database-specific bulk insert methods
        if (engineType == DatabaseEngineType.PostgreSQL)
        {
            return await ExecutePostgresInsert(connection, transaction, tableName, data, errorDetails);
        }
        else if (engineType == DatabaseEngineType.MSSQL)
        {
            return await ExecuteSqlServerInsert(connection, tableName, data, errorDetails);
        }
        else
        {
            return await ExecuteGenericBulkInsert(connection, transaction, tableName, data, engineType, errorDetails);
        }

    }

    private async Task<(int rowsWritten, int rowsFailed, List<string> errorDetails)> ExecuteUpserts(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        List<Dictionary<string, object?>> data,
        DatabaseEngineType engineType)
    {
        var errorDetails = new List<string>();

        if (!data.Any())
            return (0, 0, errorDetails);

        try
        {
            // Get primary key columns for the table
            var primaryKeyColumns = await GetPrimaryKeyColumns(connection, tableName, engineType);

            if (!primaryKeyColumns.Any())
            {
                logger.LogWarning("No primary key found for table {Table}, falling back to insert mode", tableName);
                return await ExecuteInserts(connection, transaction, tableName, data, engineType);
            }

            // Validate that all rows have the primary key columns
            var missingKeys = data.Where(row => !primaryKeyColumns.All(pk => row.ContainsKey(pk))).ToList();
            if (missingKeys.Any())
            {
                var errorMsg = $"Some rows are missing primary key columns: {string.Join(", ", primaryKeyColumns)}";
                logger.LogError(errorMsg);
                throw new InvalidOperationException(errorMsg);
            }

            // Use database-specific bulk upsert methods
            if (engineType == DatabaseEngineType.PostgreSQL)
            {
                return await ExecutePostgresUpsert(connection, transaction, tableName, data, primaryKeyColumns, errorDetails);
            }
            else if (engineType == DatabaseEngineType.MSSQL)
            {
                return await ExecuteSqlServerUpsert(connection, tableName, data, errorDetails);
            }

            // Use temp table + merge approach for other databases (MySQL)
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
                var mergeQuery = BuildMergeQuery(tableName, tempTableName, columns, primaryKeyColumns, engineType);

                logger.LogDebug("Executing merge query: {MergeQuery}", mergeQuery);

                try
                {
                    await connection.ExecuteAsync(mergeQuery, transaction: transaction);
                }
                catch (Exception mergeEx)
                {
                    logger.LogError(mergeEx, "Merge query failed. Query: {Query}", mergeQuery);
                    throw new InvalidOperationException($"Merge query failed: {mergeEx.Message}. Query: {mergeQuery}", mergeEx);
                }

                logger.LogInformation("Bulk upserted {RowCount} rows into {Table} via temp table", data.Count, tableName);
                return (data.Count, 0, errorDetails);
            }
            finally
            {
                // Clean up temp table
                try
                {
                    if (engineType != DatabaseEngineType.PostgreSQL) // PostgreSQL auto-drops temp tables
                    {
                        await connection.ExecuteAsync($"DROP TABLE IF EXISTS {SqlIdentifierGuard.Validate(tempTableName, "temp table")}", transaction: transaction);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Failed to drop temp table {TempTable}: {Error}", tempTableName, ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Upsert operation failed: {ex.Message}", ex);
        }
    }

    private async Task<(int rowsWritten, int rowsFailed, List<string> errorDetails)> ExecutePostgresInsert(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        List<Dictionary<string, object?>> data,
        List<string> errorDetails)
    {
        try
        {
            var npgsqlConnection = connection as Npgsql.NpgsqlConnection;
            if (npgsqlConnection == null)
            {
                throw new InvalidOperationException("Connection is not a PostgreSQL connection");
            }

            var columns = data.First().Keys.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
            var quotedColumns = string.Join(", ", columns.Select(x => QuoteIdentifier(x, DatabaseEngineType.PostgreSQL)));

            // Use PostgreSQL COPY for fast bulk insert
            var copyCommand = $"COPY {ValidateTable(tableName)} ({quotedColumns}) FROM STDIN (FORMAT BINARY)";

            await using var import = await npgsqlConnection.BeginBinaryImportAsync(copyCommand);

            foreach (var row in data)
            {
                await import.StartRowAsync();
                foreach (var col in columns)
                {
                    var value = row.ContainsKey(col) ? row[col] : null;
                    await import.WriteAsync(value);
                }
            }

            await import.CompleteAsync();
            var rowsWritten = data.Count; // COPY doesn't return rows imported, use data count

            logger.LogInformation("Bulk inserted {RowCount} rows into {Table} using PostgreSQL COPY", rowsWritten, tableName);
            return (rowsWritten, 0, errorDetails);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PostgreSQL bulk insert failed for table {Table}", tableName);
            throw new InvalidOperationException($"PostgreSQL bulk insert failed: {ex.Message}", ex);
        }
    }

    private async Task<(int rowsWritten, int rowsFailed, List<string> errorDetails)> ExecuteSqlServerInsert(
        DbConnection connection,
        string tableName,
        List<Dictionary<string, object?>> data,
        List<string> errorDetails)
    {
        try
        {
            var (schema, table) = ParseSchemaAndTableName(tableName);
            var entities = ConvertToExpandoObjects(data);

            // Get connection string from the connection
            var connectionString = connection.ConnectionString;

            using var dataTransferManager = new SqlServerDataTransferManager(connectionString);
            dataTransferManager.BulkInsert(entities, table, schema);

            logger.LogInformation("Bulk inserted {RowCount} rows into {Table} using SqlServerDataTransferManager", data.Count, tableName);
            return await Task.FromResult((data.Count, 0, errorDetails));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SQL Server bulk insert failed for table {Table}", tableName);
            throw new InvalidOperationException($"SQL Server bulk insert failed: {ex.Message}", ex);
        }
    }

    private async Task<(int rowsWritten, int rowsFailed, List<string> errorDetails)> ExecutePostgresUpsert(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        List<Dictionary<string, object?>> data,
        List<string> primaryKeyColumns,
        List<string> errorDetails)
    {
        try
        {
            var npgsqlConnection = connection as Npgsql.NpgsqlConnection;
            if (npgsqlConnection == null)
            {
                throw new InvalidOperationException("Connection is not a PostgreSQL connection");
            }

            var columns = data.First().Keys.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
            var tempTableName = $"temp_{Guid.NewGuid():N}";

            // Step 1: Create temp table
            var quotedColumns = string.Join(", ", columns.Select(x => QuoteIdentifier(x, DatabaseEngineType.PostgreSQL)));
            var validatedTable = ValidateTable(tableName);
            var createTempQuery = $"CREATE TEMP TABLE {tempTableName} AS SELECT {quotedColumns} FROM {validatedTable} LIMIT 0";
            await connection.ExecuteAsync(createTempQuery, transaction: transaction);

            // Step 2: Bulk insert into temp table using COPY
            var copyCommand = $"COPY {tempTableName} ({quotedColumns}) FROM STDIN (FORMAT BINARY)";
            await using (var import = await npgsqlConnection.BeginBinaryImportAsync(copyCommand))
            {
                foreach (var row in data)
                {
                    await import.StartRowAsync();
                    foreach (var col in columns)
                    {
                        var value = row.ContainsKey(col) ? row[col] : null;
                        await import.WriteAsync(value);
                    }
                }

                await import.CompleteAsync();
            }

            // Step 3: Merge from temp table to destination
            var nonKeyColumns = columns.Except(primaryKeyColumns).ToList();
            var quotedPrimaryKeys = string.Join(", ", primaryKeyColumns.Select(x => QuoteIdentifier(x, DatabaseEngineType.PostgreSQL)));
            var updateSet = nonKeyColumns.Any()
                ? string.Join(", ", nonKeyColumns.Select(x =>
                    $"{QuoteIdentifier(x, DatabaseEngineType.PostgreSQL)} = EXCLUDED.{QuoteIdentifier(x, DatabaseEngineType.PostgreSQL)}"))
                : $"{QuoteIdentifier(primaryKeyColumns.First(), DatabaseEngineType.PostgreSQL)} = EXCLUDED.{QuoteIdentifier(primaryKeyColumns.First(), DatabaseEngineType.PostgreSQL)}";

            var mergeQuery = $@"
INSERT INTO {validatedTable} ({quotedColumns})
SELECT {quotedColumns} FROM {tempTableName}
ON CONFLICT ({quotedPrimaryKeys})
DO UPDATE SET {updateSet}";

            await connection.ExecuteAsync(mergeQuery, transaction: transaction);

            logger.LogInformation("Bulk upserted {RowCount} rows into {Table} using PostgreSQL COPY + MERGE", data.Count, tableName);
            return (data.Count, 0, errorDetails);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PostgreSQL bulk upsert failed for table {Table}", tableName);
            throw new InvalidOperationException($"PostgreSQL bulk upsert failed: {ex.Message}", ex);
        }
    }

    private async Task<(int rowsWritten, int rowsFailed, List<string> errorDetails)> ExecuteSqlServerUpsert(
        DbConnection connection,
        string tableName,
        List<Dictionary<string, object?>> data,
        List<string> errorDetails)
    {
        try
        {
            var (schema, table) = ParseSchemaAndTableName(tableName);
            var entities = ConvertToExpandoObjects(data);

            // Get connection string from the connection
            var connectionString = connection.ConnectionString;

            using var dataTransferManager = new SqlServerDataTransferManager(connectionString);

            // Use MergeData for upsert - overwriteDestination=false, updateOnlyChangedRows=true
            dataTransferManager.MergeData(entities, table, schema, overwriteDestination: false, updateOnlyChangedRows: true);

            logger.LogInformation("Bulk upserted {RowCount} rows into {Table} using SqlServerDataTransferManager", data.Count, tableName);
            return await Task.FromResult((data.Count, 0, errorDetails));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SQL Server bulk upsert failed for table {Table}", tableName);
            throw new InvalidOperationException($"SQL Server bulk upsert failed: {ex.Message}", ex);
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
            logger.LogError(ex, "Bulk insert failed for table {Table}, falling back to row-by-row", tableName);

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
                    logger.LogWarning("Failed to insert row into {Table}: {Error}", tableName, rowEx.Message);

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
        var options = engineType switch
        {
            DatabaseEngineType.PostgreSQL => new DbContextOptionsBuilder<DynamicDbContext>()
                .UseNpgsql(connection)
                .Options,
            DatabaseEngineType.MySQL => new DbContextOptionsBuilder<DynamicDbContext>()
                .UseMySql(connection, ServerVersion.AutoDetect(connection.ConnectionString))
                .Options,
            DatabaseEngineType.MSSQL => new DbContextOptionsBuilder<DynamicDbContext>()
                .UseSqlServer(connection)
                .Options,
            _ => throw new NotSupportedException($"Database engine type '{engineType}' is not supported")
        };

        var context = new DynamicDbContext(options, tableName, primaryKeys);
        context.Database.UseTransaction(transaction);
        return context;
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
            // PostgreSQL: Don't quote temp table name - let it be lowercase
            DatabaseEngineType.PostgreSQL => $"CREATE TEMP TABLE {tempTableName} AS SELECT * FROM {validatedSource} LIMIT 0",
            DatabaseEngineType.MySQL => $"CREATE TEMPORARY TABLE {tempTableName} LIKE {validatedSource}",
            DatabaseEngineType.MSSQL => $"SELECT * INTO {tempTableName} FROM {validatedSource} WHERE 1=0",
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
            DatabaseEngineType.PostgreSQL => BuildPostgreSqlMerge(destinationTable, sourceTable, columns, primaryKeyColumns, nonKeyColumns),
            DatabaseEngineType.MySQL => BuildMySqlMerge(destinationTable, sourceTable, columns, primaryKeyColumns, nonKeyColumns),
            DatabaseEngineType.MSSQL => BuildSqlServerMerge(destinationTable, sourceTable, columns, primaryKeyColumns, nonKeyColumns),
            _ => throw new NotSupportedException($"Database engine type '{engineType}' is not supported")
        };
    }

    private string BuildPostgreSqlMerge(string destinationTable, string sourceTable, List<string> columns, List<string> primaryKeyColumns, List<string> nonKeyColumns)
    {
        // Quote identifiers to handle case-sensitive and reserved words
        var quotedColumns = columns.Select(x => QuoteIdentifier(x, DatabaseEngineType.PostgreSQL)).ToList();
        var columnList = string.Join(", ", quotedColumns);
        var quotedPrimaryKeys = primaryKeyColumns.Select(x => QuoteIdentifier(x, DatabaseEngineType.PostgreSQL)).ToList();

        var updateSet = nonKeyColumns.Any()
            ? string.Join(", ", nonKeyColumns.Select(x =>
                $"{QuoteIdentifier(x, DatabaseEngineType.PostgreSQL)} = EXCLUDED.{QuoteIdentifier(x, DatabaseEngineType.PostgreSQL)}"))
            : $"{QuoteIdentifier(primaryKeyColumns.First(), DatabaseEngineType.PostgreSQL)} = EXCLUDED.{QuoteIdentifier(primaryKeyColumns.First(), DatabaseEngineType.PostgreSQL)}"; // Dummy update if no non-key columns

        // Quote table names properly
        var validatedDestination = ValidateTable(destinationTable);
        var quotedSourceTable = sourceTable.Contains(".") ? ValidateTable(sourceTable) : QuoteIdentifier(sourceTable, DatabaseEngineType.PostgreSQL);

        return $@"
INSERT INTO {validatedDestination} ({columnList})
SELECT {columnList} FROM {quotedSourceTable}
ON CONFLICT ({string.Join(", ", quotedPrimaryKeys)})
DO UPDATE SET {updateSet}";
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

    private string BuildSqlServerMerge(string destinationTable, string sourceTable, List<string> columns, List<string> primaryKeyColumns, List<string> nonKeyColumns)
    {
        foreach (var x in columns)
        {
            SqlIdentifierGuard.Validate(x, "column");
        }
        foreach (var x in primaryKeyColumns)
        {
            SqlIdentifierGuard.Validate(x, "column");
        }
        var validatedDestination = ValidateTable(destinationTable);
        var validatedSource = ValidateTable(sourceTable);

        var keyConditions = string.Join(" AND ", primaryKeyColumns.Select(x => $"target.{x} = source.{x}"));
        var insertColumns = string.Join(", ", columns);
        var insertValues = string.Join(", ", columns.Select(x => $"source.{x}"));
        var updateSet = nonKeyColumns.Any()
            ? string.Join(", ", nonKeyColumns.Select(x => $"target.{x} = source.{x}"))
            : $"target.{primaryKeyColumns.First()} = source.{primaryKeyColumns.First()}"; // Dummy update

        return $@"
            MERGE {validatedDestination} AS target
            USING {validatedSource} AS source
            ON {keyConditions}
            WHEN MATCHED THEN
                UPDATE SET {updateSet}
            WHEN NOT MATCHED THEN
                INSERT ({insertColumns})
                VALUES ({insertValues});";
    }

    private async Task<List<string>> GetPrimaryKeyColumns(DbConnection connection, string tableName, DatabaseEngineType engineType)
    {
        var (schema, table) = ParseSchemaAndTableName(tableName);

        var query = engineType switch
        {
            DatabaseEngineType.PostgreSQL => schema != null
                ? @"SELECT a.attname
                    FROM pg_index i
                    JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey)
                    WHERE i.indrelid = @tableName::regclass AND i.indisprimary
                    ORDER BY array_position(i.indkey, a.attnum)"
                : @"SELECT a.attname
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

            DatabaseEngineType.MSSQL => schema != null
                ? @"SELECT c.name
                    FROM sys.indexes i
                    INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                    INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                    INNER JOIN sys.tables t ON i.object_id = t.object_id
                    INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
                    WHERE i.is_primary_key = 1 AND s.name = @schema AND t.name = @table
                    ORDER BY ic.key_ordinal"
                : @"SELECT c.name
                    FROM sys.indexes i
                    INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                    INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                    INNER JOIN sys.tables t ON i.object_id = t.object_id
                    WHERE i.is_primary_key = 1 AND t.name = @table
                    ORDER BY ic.key_ordinal",

            _ => throw new NotSupportedException($"Database engine type '{engineType}' is not supported")
        };

        try
        {
            if (engineType == DatabaseEngineType.PostgreSQL)
            {
                // PostgreSQL uses $1 parameter, pass the full table name
                var fullTableName = schema != null ? $"{schema}.{table}" : table;
                var result = await connection.QueryAsync<string>(query, new { tableName = fullTableName });
                return result.ToList();
            }
            else
            {
                var result = await connection.QueryAsync<string>(query, new { schema, table });
                return result.ToList();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retrieve primary key columns for table {Table}", tableName);
            return new List<string>();
        }
    }

    // §1.10 — Identifiers (schema / table / column names) cannot be parameterized, so every
    // identifier interpolated into SQL text below is whitelist-validated via SqlIdentifierGuard
    // before it reaches a query string. Where an identifier is wrapped in quotes, the embedded
    // quote char is doubled as defence-in-depth.

    private static string QuoteIdentifier(string identifier, DatabaseEngineType engineType)
    {
        SqlIdentifierGuard.Validate(identifier, "identifier");

        return engineType switch
        {
            DatabaseEngineType.PostgreSQL => $"\"{SqlIdentifierGuard.EscapeQuote(identifier, '"')}\"",
            DatabaseEngineType.MSSQL => $"[{identifier.Replace("]", "]]")}]",
            DatabaseEngineType.MySQL => $"`{SqlIdentifierGuard.EscapeQuote(identifier, '`')}`",
            _ => identifier
        };
    }

    private static string ValidateAndQuoteTable(string tableName, DatabaseEngineType engineType)
    {
        var parts = tableName.Split('.', 2);
        if (parts.Length == 2)
        {
            return $"{QuoteIdentifier(parts[0], engineType)}.{QuoteIdentifier(parts[1], engineType)}";
        }

        return QuoteIdentifier(tableName, engineType);
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

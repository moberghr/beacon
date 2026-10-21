using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Logging;
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

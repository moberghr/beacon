using Beacon.Core.Data.Enums;
using Beacon.Core.DataMigration;
using Beacon.Core.Helpers;
using Beacon.Core.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Beacon.Connector.SqlServer;

/// <summary>
/// Writes migration rows into SQL Server with <see cref="SqlBulkCopy"/>, on the connection and inside the transaction Core
/// opened. Insert copies straight into the table, keeping identity values and NULLs; Upsert copies into a temporary staging
/// table with the destination's columns and moves the rows with a <c>MERGE</c> on the primary key; Clear truncates
/// (transactional on SQL Server).
/// </summary>
internal sealed class SqlServerMigrationBulkWriter(ILogger<SqlServerMigrationBulkWriter> logger) : IMigrationBulkWriter
{
    public DatabaseEngineType Engine => DatabaseEngineType.MSSQL;

    public async Task ClearAsync(MigrationWriteTarget target, CancellationToken cancellationToken)
    {
        var (connection, transaction) = Require(target);

        await ExecuteAsync(connection, transaction, SqlServerMigrationSql.Truncate(target.Schema, target.Table), target, cancellationToken);
        logger.LogInformation("Truncated table {Table}", ForLog(target));
    }

    public async Task<int> InsertAsync(MigrationWriteTarget target, MigrationRows rows, CancellationToken cancellationToken)
    {
        var (connection, transaction) = Require(target);
        var destination = await DestinationColumnsAsync(connection, transaction, target, cancellationToken);

        var copied = await CopyAsync(
            connection,
            transaction,
            SqlServerMigrationSql.Table(target.Schema, target.Table),
            rows,
            destination.NamesOf(rows.Columns),
            target,
            cancellationToken);
        logger.LogInformation("Copied {RowCount} rows into {Table}", copied, ForLog(target));

        return copied;
    }

    public async Task<int> UpsertAsync(MigrationWriteTarget target, MigrationRows rows, IReadOnlyList<string> keyColumns, CancellationToken cancellationToken)
    {
        if (keyColumns.Count == 0)
        {
            throw new InvalidOperationException("An upsert needs the table's primary key columns.");
        }

        var (connection, transaction) = Require(target);
        var destination = await DestinationColumnsAsync(connection, transaction, target, cancellationToken);
        var columns = destination.NamesOf(rows.Columns);
        var stageName = SqlServerMigrationSql.NewStageName();

        try
        {
            await ExecuteAsync(connection, transaction, SqlServerMigrationSql.CreateStage(stageName, target.Schema, target.Table, columns), target, cancellationToken);
            var staged = await CopyAsync(connection, transaction, SqlServerMigrationSql.StageTable(stageName), rows, columns, target, cancellationToken);
            var affected = await ExecuteAsync(
                connection,
                transaction,
                SqlServerMigrationSql.MergeFromStage(
                    stageName,
                    target.Schema,
                    target.Table,
                    columns,
                    keyColumns,
                    destination.IdentityColumns,
                    destination.NonComparableColumns),
                target,
                cancellationToken);
            logger.LogInformation("Upserted {RowCount} rows into {Table} ({AffectedCount} affected)", staged, ForLog(target), affected);

            return staged;
        }
        finally
        {
            await CleanUpAsync(connection, transaction, stageName, SqlServerMigrationSql.InsertsIdentity(columns, destination.IdentityColumns), target);
        }
    }

    private static (SqlConnection Connection, SqlTransaction Transaction) Require(MigrationWriteTarget target)
    {
        if (target.Connection is not SqlConnection connection)
        {
            throw new InvalidOperationException("The SQL Server migration writer needs a SqlClient connection.");
        }

        if (target.Transaction is not SqlTransaction transaction || !ReferenceEquals(transaction.Connection, connection))
        {
            throw new InvalidOperationException("The SQL Server migration writer needs a transaction open on its connection.");
        }

        return (connection, transaction);
    }

    private static async Task<int> ExecuteAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        MigrationWriteTarget target,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction)
        {
            CommandTimeout = target.CommandTimeoutSeconds
        };

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Read inside the run's transaction; the table goes in as a parameter (its quoted name), never into the text.
    private static async Task<DestinationColumns> DestinationColumnsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MigrationWriteTarget target,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(SqlServerMigrationSql.DestinationColumns(), connection, transaction)
        {
            CommandTimeout = target.CommandTimeoutSeconds
        };
        command.Parameters.Add(new SqlParameter(SqlServerMigrationSql.TableParameter, System.Data.SqlDbType.NVarChar, 776)
        {
            Value = SqlServerMigrationSql.Table(target.Schema, target.Table)
        });

        var columns = new List<DestinationColumn>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(new DestinationColumn(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2)));
        }

        return new DestinationColumns(columns);
    }

    // Identity values are kept and NULLs stay NULL (a missing value is NULL, never the column's default); the table's insert
    // triggers fire, as they did before the writer moved here. Rows stream from the reader; each field maps by position to its
    // destination column's name (SqlBulkCopy matches names case-sensitively).
    private static async Task<int> CopyAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string tableSql,
        MigrationRows rows,
        IReadOnlyList<string> destinationColumns,
        MigrationWriteTarget target,
        CancellationToken cancellationToken)
    {
        using var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.KeepNulls | SqlBulkCopyOptions.FireTriggers, transaction)
        {
            DestinationTableName = tableSql,
            EnableStreaming = true,
            BulkCopyTimeout = target.CommandTimeoutSeconds
        };
        for (var i = 0; i < destinationColumns.Count; i++)
        {
            bulkCopy.ColumnMappings.Add(i, destinationColumns[i]);
        }

        await using var reader = new MigrationRowReader(rows);
        await bulkCopy.WriteToServerAsync(reader, cancellationToken);

        return checked((int)bulkCopy.RowsCopied64);
    }

    // Best effort: a failure here is logged, never thrown, so it cannot hide the exception that ended the upsert. The
    // transaction's rollback removes the stage in any case.
    private async Task CleanUpAsync(SqlConnection connection, SqlTransaction transaction, string stageName, bool identityInsert, MigrationWriteTarget target)
    {
        var statements = identityInsert
            ? new[] { SqlServerMigrationSql.IdentityInsert(target.Schema, target.Table, false), SqlServerMigrationSql.DropStage(stageName) }
            : new[] { SqlServerMigrationSql.DropStage(stageName) };
        foreach (var statement in statements)
        {
            try
            {
                await ExecuteAsync(connection, transaction, statement, target, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Clean-up after an upsert into {Table} failed ({Failure})", ForLog(target), FailureCodes.Describe(ex));
            }
        }
    }

    private static string ForLog(MigrationWriteTarget target)
    {
        return SqlIdentifierGuard.ForLog(target.Schema == null ? target.Table : $"{target.Schema}.{target.Table}");
    }

    private sealed record DestinationColumn(string Name, bool IsIdentity, bool IsComparable);

    /// <summary>
    /// The destination table's columns, as SQL Server spells them, which of them are identity columns and which cannot be
    /// compared.
    /// </summary>
    private sealed class DestinationColumns(IReadOnlyList<DestinationColumn> columns)
    {
        public IReadOnlyList<string> IdentityColumns { get; } = columns
            .Where(x => x.IsIdentity)
            .Select(x => x.Name)
            .ToList();

        public IReadOnlyList<string> NonComparableColumns { get; } = columns
            .Where(x => !x.IsComparable)
            .Select(x => x.Name)
            .ToList();

        /// <summary>
        /// Each of <paramref name="names"/> spelled as the table spells it: matched exactly first, then ignoring case (how SQL
        /// Server compares names by default). A name the table does not have is kept, so the copy names it when it fails.
        /// </summary>
        public IReadOnlyList<string> NamesOf(IReadOnlyList<string> names)
        {
            return names
                .Select(x => Find(x, StringComparison.Ordinal) ?? Find(x, StringComparison.OrdinalIgnoreCase) ?? x)
                .ToList();
        }

        private string? Find(string name, StringComparison comparison)
        {
            return columns
                .Where(x => string.Equals(x.Name, name, comparison))
                .Select(x => x.Name)
                .FirstOrDefault();
        }
    }
}

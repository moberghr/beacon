using Beacon.Core.Data.Enums;
using Beacon.Core.DataMigration;
using Beacon.Core.Services;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Beacon.Connector.PostgreSql;

/// <summary>
/// Writes migration rows into PostgreSQL with binary COPY, on the connection and inside the transaction Core opened.
/// Insert copies straight into the table; Upsert copies into a temporary staging table with the written columns and moves
/// the rows with <c>INSERT … ON CONFLICT</c> on the primary key; Clear truncates (transactional on PostgreSQL).
/// </summary>
internal sealed class PostgreSqlMigrationBulkWriter(ILogger<PostgreSqlMigrationBulkWriter> logger) : IMigrationBulkWriter
{
    public DatabaseEngineType Engine => DatabaseEngineType.PostgreSQL;

    public async Task ClearAsync(MigrationWriteTarget target, CancellationToken cancellationToken)
    {
        var (connection, transaction) = Require(target);

        await ExecuteAsync(connection, transaction, PostgreSqlMigrationSql.Truncate(target.Schema, target.Table), target, cancellationToken);
        logger.LogInformation("Truncated table {Table}", ForLog(target));
    }

    public async Task<int> InsertAsync(MigrationWriteTarget target, MigrationRows rows, CancellationToken cancellationToken)
    {
        var (connection, _) = Require(target);

        var copied = await CopyAsync(connection, PostgreSqlMigrationSql.Table(target.Schema, target.Table), rows, target, cancellationToken);
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
        var stageName = PostgreSqlMigrationSql.NewStageName();

        await ExecuteAsync(connection, transaction, PostgreSqlMigrationSql.CreateStage(stageName, target.Schema, target.Table, rows.Columns), target, cancellationToken);
        var staged = await CopyAsync(connection, PostgreSqlMigrationSql.Table(null, stageName), rows, target, cancellationToken);
        var affected = await ExecuteAsync(
            connection,
            transaction,
            PostgreSqlMigrationSql.MergeFromStage(stageName, target.Schema, target.Table, rows.Columns, keyColumns),
            target,
            cancellationToken);

        // Each chunk drops its stage at once rather than holding it, and its catalog entry, until the transaction ends. A
        // failure before this point aborts the transaction, whose rollback drops the stage with it.
        await ExecuteAsync(connection, transaction, PostgreSqlMigrationSql.DropStage(stageName), target, cancellationToken);
        logger.LogInformation("Upserted {RowCount} rows into {Table} ({AffectedCount} affected)", staged, ForLog(target), affected);

        return staged;
    }

    private static (NpgsqlConnection Connection, NpgsqlTransaction Transaction) Require(MigrationWriteTarget target)
    {
        if (target.Connection is not NpgsqlConnection connection)
        {
            throw new InvalidOperationException("The PostgreSQL migration writer needs an Npgsql connection.");
        }

        if (target.Transaction is not NpgsqlTransaction transaction || !ReferenceEquals(transaction.Connection, connection))
        {
            throw new InvalidOperationException("The PostgreSQL migration writer needs a transaction open on its connection.");
        }

        return (connection, transaction);
    }

    private static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        MigrationWriteTarget target,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction)
        {
            CommandTimeout = target.CommandTimeoutSeconds
        };

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // COPY runs inside the connection's open transaction. Each value is written with the type Npgsql infers from its CLR
    // type; a missing column or a null writes NULL.
    private static async Task<int> CopyAsync(
        NpgsqlConnection connection,
        string tableSql,
        MigrationRows rows,
        MigrationWriteTarget target,
        CancellationToken cancellationToken)
    {
        await using var import = await connection.BeginBinaryImportAsync(PostgreSqlMigrationSql.CopyInto(tableSql, rows.Columns), cancellationToken);
        import.Timeout = TimeSpan.FromSeconds(target.CommandTimeoutSeconds);

        foreach (var row in rows.Rows)
        {
            await import.StartRowAsync(cancellationToken);
            foreach (var column in rows.Columns)
            {
                var value = row.TryGetValue(column, out var found) ? found : null;
                await import.WriteAsync(value, cancellationToken);
            }
        }

        var copied = await import.CompleteAsync(cancellationToken);

        return checked((int)copied);
    }

    private static string ForLog(MigrationWriteTarget target)
    {
        return SqlIdentifierGuard.ForLog(target.Schema == null ? target.Table : $"{target.Schema}.{target.Table}");
    }
}

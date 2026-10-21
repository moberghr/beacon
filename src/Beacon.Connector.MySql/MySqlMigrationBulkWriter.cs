using System.Data.Common;
using System.Text.Json;
using Beacon.Core.Data.Enums;
using Beacon.Core.DataMigration;
using Beacon.Core.Services;
using Microsoft.Extensions.Logging;

namespace Beacon.Connector.MySql;

/// <summary>
/// Writes migration rows into MySQL with multi-row parameterised <c>INSERT</c> commands, on the connection and inside the
/// transaction Core opened. Upsert adds <c>ON DUPLICATE KEY UPDATE</c>; Clear runs <c>TRUNCATE TABLE</c>, which the server
/// refuses for a table other tables reference by foreign key, and then starts the transaction again (TRUNCATE commits it
/// implicitly), so a run that fails after the clear leaves the table empty, never partly written. Written against ADO.NET's
/// base types only, so it does not depend on the MySQL driver.
/// </summary>
internal sealed class MySqlMigrationBulkWriter(ILogger<MySqlMigrationBulkWriter> logger) : IMigrationBulkWriter
{
    public DatabaseEngineType Engine => DatabaseEngineType.MySQL;

    public async Task ClearAsync(MigrationWriteTarget target, CancellationToken cancellationToken)
    {
        Require(target);

        // TRUNCATE commits the run's transaction implicitly; a transaction is started again on the same connection so the
        // rows written next are committed or rolled back with the run. A failure after this point leaves the table empty.
        await using var command = Command(target, MySqlMigrationSql.Clear(target.Schema, target.Table));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await using var restart = Command(target, MySqlMigrationSql.StartTransaction());
        await restart.ExecuteNonQueryAsync(cancellationToken);
        logger.LogInformation("Truncated {Table}", ForLog(target));
    }

    public async Task<int> InsertAsync(MigrationWriteTarget target, MigrationRows rows, CancellationToken cancellationToken)
    {
        Require(target);

        var (written, _) = await WriteAsync(target, rows, null, cancellationToken);
        logger.LogInformation("Inserted {RowCount} rows into {Table}", written, ForLog(target));

        return written;
    }

    public async Task<int> UpsertAsync(MigrationWriteTarget target, MigrationRows rows, IReadOnlyList<string> keyColumns, CancellationToken cancellationToken)
    {
        if (keyColumns.Count == 0)
        {
            throw new InvalidOperationException("An upsert needs the table's primary key columns.");
        }

        Require(target);

        // MySQL reports 1 affected row per inserted row and 2 per updated one; the count returned is the rows processed.
        var (written, affected) = await WriteAsync(target, rows, keyColumns, cancellationToken);
        logger.LogInformation("Upserted {RowCount} rows into {Table} ({AffectedCount} affected)", written, ForLog(target), affected);

        return written;
    }

    /// <summary>
    /// The value as the driver binds it correctly. MySql.Data formats a <see cref="DateOnly"/> with the current culture (the
    /// server refuses it), sends a <see cref="TimeOnly"/> without its seconds, and cannot bind a <see cref="JsonElement"/>:
    /// those become a <see cref="DateTime"/>, a <see cref="TimeSpan"/> and the JSON's value or text. A JSON string goes as
    /// its string, a JSON boolean as a boolean, a JSON null as NULL, a number, an object or an array as its JSON text. Every
    /// other value is bound as it is.
    /// </summary>
    internal static object Bindable(object? value)
    {
        return value switch
        {
            null => DBNull.Value,
            DateOnly date => date.ToDateTime(TimeOnly.MinValue),
            TimeOnly time => time.ToTimeSpan(),
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString()!,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => DBNull.Value,
            JsonElement json => json.GetRawText(),
            _ => value
        };
    }

    private static void Require(MigrationWriteTarget target)
    {
        if (!ReferenceEquals(target.Transaction.Connection, target.Connection))
        {
            throw new InvalidOperationException("The MySQL migration writer needs a transaction open on its connection.");
        }
    }

    // One command per batch of rows, every value a parameter; a missing column or a null writes NULL.
    private static async Task<(int Written, int Affected)> WriteAsync(
        MigrationWriteTarget target,
        MigrationRows rows,
        IReadOnlyList<string>? keyColumns,
        CancellationToken cancellationToken)
    {
        var written = 0;
        var affected = 0;
        foreach (var batch in Batches(rows))
        {
            var sql = keyColumns == null
                ? MySqlMigrationSql.Insert(target.Schema, target.Table, rows.Columns, batch.Count)
                : MySqlMigrationSql.Upsert(target.Schema, target.Table, rows.Columns, keyColumns, batch.Count);
            await using var command = Command(target, sql);
            var index = 0;
            foreach (var value in batch.SelectMany(x => x))
            {
                AddParameter(command, MySqlMigrationSql.ParameterName(index++), value);
            }

            affected += await command.ExecuteNonQueryAsync(cancellationToken);
            written += batch.Count;
        }

        return (written, affected);
    }

    /// <summary>
    /// The rows in order, each as its bindable values in column order (a missing column is NULL), split so that no command
    /// carries more than <see cref="MySqlMigrationSql.MaxRowsPerCommand"/> rows,
    /// <see cref="MySqlMigrationSql.MaxParametersPerCommand"/> parameters or
    /// <see cref="MySqlMigrationSql.MaxPayloadBytesPerCommand"/> of estimated payload. A row is never split: one that alone
    /// exceeds the payload goes in a command of its own.
    /// </summary>
    private static IEnumerable<List<object[]>> Batches(MigrationRows rows)
    {
        var batch = new List<object[]>();
        long payload = 0;
        foreach (var row in rows.Rows)
        {
            var values = rows.Columns
                .Select(x => Bindable(row.TryGetValue(x, out var value) ? value : null))
                .ToArray();
            var rowPayload = values.Sum(MySqlMigrationSql.EstimatedSize);
            var full = batch.Count == MySqlMigrationSql.MaxRowsPerCommand
                || (batch.Count + 1) * rows.Columns.Count > MySqlMigrationSql.MaxParametersPerCommand
                || payload + rowPayload > MySqlMigrationSql.MaxPayloadBytesPerCommand;
            if (full && batch.Count > 0)
            {
                yield return batch;
                batch = [];
                payload = 0;
            }

            batch.Add(values);
            payload += rowPayload;
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static DbCommand Command(MigrationWriteTarget target, string sql)
    {
        var command = target.Connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = target.Transaction;
        command.CommandTimeout = target.CommandTimeoutSeconds;

        return command;
    }

    private static string ForLog(MigrationWriteTarget target)
    {
        return SqlIdentifierGuard.ForLog(target.Schema == null ? target.Table : $"{target.Schema}.{target.Table}");
    }
}

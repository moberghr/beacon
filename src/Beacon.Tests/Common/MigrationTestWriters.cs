using Beacon.Core.Data.Enums;
using Beacon.Core.DataMigration;

namespace Beacon.Tests.Common;

/// <summary>
/// Migration writers for tests that build <c>MigrationService</c>: one recording writer per engine a write target may have.
/// Clearing sends <c>TRUNCATE TABLE &lt;table&gt;</c> on the target's connection inside its transaction (so a recording
/// connection sees it); inserts and upserts are recorded, not sent. No database (§4.7).
/// </summary>
internal static class MigrationTestWriters
{
    /// <summary>A writer for PostgreSQL, SQL Server and MySQL.</summary>
    public static MigrationBulkWriters All() =>
        new([
            new RecordingMigrationWriter(DatabaseEngineType.PostgreSQL),
            new RecordingMigrationWriter(DatabaseEngineType.MSSQL),
            new RecordingMigrationWriter(DatabaseEngineType.MySQL)
        ]);

    /// <summary>Exactly the given writers.</summary>
    public static MigrationBulkWriters Of(params IMigrationBulkWriter[] writers) => new(writers);
}

/// <summary>A recording <see cref="IMigrationBulkWriter"/>; see <see cref="MigrationTestWriters"/>.</summary>
internal sealed class RecordingMigrationWriter(DatabaseEngineType engine) : IMigrationBulkWriter
{
    public DatabaseEngineType Engine => engine;

    /// <summary>Every call in order: the operation (clear, insert, upsert), its target, its rows and key columns.</summary>
    public List<(string Operation, MigrationWriteTarget Target, MigrationRows? Rows, IReadOnlyList<string>? KeyColumns)> Calls { get; } = [];

    /// <summary>What an insert or upsert throws, after it is recorded; null writes.</summary>
    public Exception? FailWritingWith { get; set; }

    public IReadOnlyList<string> Operations => Calls
        .Select(x => x.Operation)
        .ToList();

    public async Task ClearAsync(MigrationWriteTarget target, CancellationToken cancellationToken)
    {
        Calls.Add(("clear", target, null, null));

        await using var command = target.Connection.CreateCommand();
        command.Transaction = target.Transaction;
        command.CommandTimeout = target.CommandTimeoutSeconds;
        command.CommandText = $"TRUNCATE TABLE {target.Table}";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<int> InsertAsync(MigrationWriteTarget target, MigrationRows rows, CancellationToken cancellationToken)
    {
        Calls.Add(("insert", target, rows, null));

        return Written(rows);
    }

    public Task<int> UpsertAsync(MigrationWriteTarget target, MigrationRows rows, IReadOnlyList<string> keyColumns, CancellationToken cancellationToken)
    {
        Calls.Add(("upsert", target, rows, keyColumns));

        return Written(rows);
    }

    private Task<int> Written(MigrationRows rows)
    {
        if (FailWritingWith != null)
        {
            throw FailWritingWith;
        }

        return Task.FromResult(rows.Rows.Count);
    }
}

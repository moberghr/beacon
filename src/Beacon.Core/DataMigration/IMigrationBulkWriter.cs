using System.Data.Common;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.DataMigration;

/// <summary>
/// Writes a migration run's rows into its destination table on one database engine. A connector package registers the
/// writer of its engine; Beacon Core owns the run around it: it opens the destination's connection and the one transaction
/// the run commits or rolls back, validates every identifier, reads the table's primary key and splits the rows into chunks.
/// </summary>
/// <remarks>
/// Invariants every writer keeps:
/// <list type="bullet">
/// <item>The schema, table and column names (and the key columns) were already validated by Core with
/// <c>SqlIdentifierGuard.Validate</c>; a writer still quotes each one for its dialect (<c>SqlIdentifierGuard.Quote</c>)
/// wherever it builds SQL text. Values never enter SQL text: they travel as parameters or as a bulk-copy payload.</item>
/// <item>A writer never opens, closes or commits a connection or a transaction: every command it sends runs on
/// <see cref="MigrationWriteTarget.Connection"/> inside <see cref="MigrationWriteTarget.Transaction"/>, with
/// <see cref="MigrationWriteTarget.CommandTimeoutSeconds"/> as its timeout. A failure throws; Core rolls the run back.</item>
/// <item>A row without a value for one of <see cref="MigrationRows.Columns"/> writes NULL into that column.</item>
/// <item>A writer logs only names made safe with <c>SqlIdentifierGuard.ForLog</c>, counts and
/// <c>FailureCodes.Describe</c> — never a row value, SQL holding values or a connection string.</item>
/// <item>A writer is registered as a singleton and keeps no state between calls: everything a call needs arrives in its
/// arguments, so it never captures a scoped service.</item>
/// </list>
/// </remarks>
public interface IMigrationBulkWriter
{
    /// <summary>The destination engine this writer serves.</summary>
    DatabaseEngineType Engine { get; }

    /// <summary>Removes every row of the destination table, inside <see cref="MigrationWriteTarget.Transaction"/>.</summary>
    Task ClearAsync(MigrationWriteTarget target, CancellationToken cancellationToken);

    /// <summary>Inserts <paramref name="rows"/> into the destination table and returns the number of rows written.</summary>
    Task<int> InsertAsync(MigrationWriteTarget target, MigrationRows rows, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts <paramref name="rows"/>, updating the row already holding the same <paramref name="keyColumns"/> values (the
    /// table's primary key, never empty), and returns the number of rows processed: every row of <paramref name="rows"/>,
    /// inserted or matched, whether or not a matched row's values changed.
    /// </summary>
    Task<int> UpsertAsync(MigrationWriteTarget target, MigrationRows rows, IReadOnlyList<string> keyColumns, CancellationToken cancellationToken);
}

/// <summary>
/// The destination of a write: the open connection and transaction Core manages, the validated table (with its schema when
/// the job names one) and the timeout of every command the writer sends.
/// </summary>
public sealed record MigrationWriteTarget(DbConnection Connection, DbTransaction Transaction, string? Schema, string Table, int CommandTimeoutSeconds);

/// <summary>
/// Rows to write: the validated column names, in the order they are written, and each row's values by column name. A row
/// without one of the columns writes NULL there.
/// </summary>
public sealed record MigrationRows(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);

namespace Beacon.Core;

/// <summary>
/// Application-wide constants for configurable values that should not be hardcoded.
/// </summary>
public static class Constants
{
    /// <summary>
    /// Query execution constants.
    /// </summary>
    public static class Query
    {
        /// <summary>
        /// Default maximum number of rows returned when no limit is specified.
        /// </summary>
        public const int DefaultMaxRows = 10_000;

        /// <summary>
        /// The default of <c>Beacon:Query:MaxExecutionRows</c> (<see cref="Configuration.QueryExecutionOptions"/>): the most
        /// rows one statement may return when Beacon runs a query for one of its own features (a subscription, a preview, an
        /// AI actor's check; a migration read has its own <c>Beacon:Migrations:MaxRows</c>). The cap is placed in the SQL itself and the read stops one row past it, and
        /// a statement whose result is larger fails instead of being cut short.
        /// </summary>
        public const int MaxExecutionRows = 100_000;

        /// <summary>
        /// The default of <c>Beacon:Query:StatementTimeoutSeconds</c> (<see cref="Configuration.QueryExecutionOptions"/>): how
        /// long one such statement may run before it is cancelled.
        /// </summary>
        public const int StatementTimeoutSeconds = 120;
    }

    /// <summary>
    /// Data migration constants.
    /// </summary>
    public static class Migration
    {
        /// <summary>
        /// Timeout in seconds for bulk copy operations.
        /// </summary>
        public const int BulkCopyTimeoutSeconds = 300;

        /// <summary>
        /// Number of rows to process per batch during bulk insert operations.
        /// </summary>
        public const int BulkInsertBatchSize = 10_000;

        /// <summary>
        /// Number of rows to process per batch during upsert operations.
        /// </summary>
        public const int UpsertBatchSize = 1_000;

        /// <summary>
        /// Maximum number of individual row failures before stopping insertion.
        /// </summary>
        public const int MaxFailedRowsBeforeStop = 100;
    }
}

namespace Semantico.Core;

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
        public const int DefaultMaxRows = 1_000_000;
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

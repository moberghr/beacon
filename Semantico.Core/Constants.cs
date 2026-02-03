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
        public const int DefaultMaxRows = 10_000;

        /// <summary>
        /// Hard limit for UI display to prevent OutOfMemoryException in Blazor Server.
        /// Results shown in data source details, query editors, and notification pages.
        /// </summary>
        public const int MaxUiDisplayRows = 100;
    }

    /// <summary>
    /// Metadata loading constants for controlling memory usage.
    /// </summary>
    public static class Metadata
    {
        /// <summary>
        /// Recommended maximum number of tables to load for large databases.
        /// Prevents memory issues with databases containing 1000+ tables.
        /// </summary>
        public const int RecommendedMaxTables = 500;

        /// <summary>
        /// Recommended maximum number of columns per table for large schemas.
        /// Prevents memory issues with wide tables (100+ columns).
        /// </summary>
        public const int RecommendedMaxColumnsPerTable = 200;
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

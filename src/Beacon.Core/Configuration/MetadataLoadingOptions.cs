namespace Beacon.Core;

/// <summary>
/// Configuration options for database metadata loading.
/// Use these settings to control memory usage when working with large databases.
/// </summary>
public class MetadataLoadingOptions
{
    /// <summary>
    /// Enables metadata loading. Set to false to completely disable database structure exploration.
    /// Default: true
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum number of tables to load per data source. Set to 0 for unlimited.
    /// Recommended: 500 for large databases to prevent memory issues.
    /// Default: 0 (unlimited)
    /// </summary>
    public int MaxTables { get; set; } = 0;

    /// <summary>
    /// Maximum number of columns to load per table. Set to 0 for unlimited.
    /// Recommended: 200 for large databases.
    /// Default: 0 (unlimited)
    /// </summary>
    public int MaxColumnsPerTable { get; set; } = 0;

    /// <summary>
    /// If true, loads only table names without column details. Significantly reduces memory usage.
    /// Default: false
    /// </summary>
    public bool LoadTableNamesOnly { get; set; } = false;

    /// <summary>
    /// List of schema names to exclude from metadata loading (case-insensitive).
    /// Useful for excluding system schemas like 'information_schema', 'pg_catalog', 'sys'.
    /// Default: empty (load all schemas)
    /// </summary>
    public List<string> ExcludeSchemas { get; set; } = new();

    /// <summary>
    /// List of schema names to include (case-insensitive). If specified, only these schemas are loaded.
    /// Takes precedence over ExcludeSchemas.
    /// Default: empty (load all schemas)
    /// </summary>
    public List<string> IncludeSchemas { get; set; } = new();
}

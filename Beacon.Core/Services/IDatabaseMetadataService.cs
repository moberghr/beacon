using Beacon.Core.Models.Metadata;

namespace Beacon.Core.Services;

public interface IDatabaseMetadataService
{
    /// <summary>
    /// Refreshes metadata for a project by querying the target database and storing the results.
    /// </summary>
    Task<DatabaseMetadataSnapshot> RefreshMetadataAsync(int projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets cached metadata for a project. If no cached data exists, it will refresh from the database.
    /// </summary>
    Task<DatabaseMetadataSnapshot> GetMetadataAsync(int projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all table names for a project.
    /// </summary>
    Task<IEnumerable<string>> GetTableNamesAsync(int projectId, string? schemaName = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all columns for a specific table.
    /// </summary>
    Task<IEnumerable<ColumnMetadataDto>> GetColumnsAsync(int projectId, string tableName, string? schemaName = null, CancellationToken cancellationToken = default);
}

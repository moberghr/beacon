using Beacon.Core.Models.Metadata;

namespace Beacon.Core.Services;

public interface IDatabaseMetadataService
{
    /// <summary>
    /// Refreshes metadata for a project by querying the target database and storing the results.
    /// When <paramref name="forceRefresh"/> is true, a warm cache is ignored and the source is
    /// re-scanned — used by the explicit "Refresh metadata" user action.
    /// </summary>
    Task<DatabaseMetadataSnapshot> RefreshMetadataAsync(int projectId, bool forceRefresh = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets cached metadata for a project. If no cached data exists, it will refresh from the database.
    /// </summary>
    Task<DatabaseMetadataSnapshot> GetMetadataAsync(int projectId, CancellationToken cancellationToken = default);
}

using Beacon.Core.Models.Metadata;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Services.Metadata;

/// <summary>
/// The work of a forced metadata refresh, in the request (<c>RefreshDataSourceMetadataCommand</c>) or as a background task
/// (<see cref="MetadataRefreshTaskExecutor"/>): re-read the catalog from the database, then sync the schema relationships.
/// The cooldown and the audit event stay with the caller.
/// </summary>
internal sealed class ForcedMetadataRefresh(
    DatabaseMetadataService metadataService,
    ISchemaRelationshipSyncService relationshipSyncService,
    ILogger<ForcedMetadataRefresh> logger)
{
    public async Task<DatabaseMetadataSnapshot> RunAsync(int dataSourceId, CancellationToken cancellationToken)
    {
        var snapshot = await metadataService.RefreshMetadataAsync(dataSourceId, forceRefresh: true, cancellationToken);

        // Relationship sync is an enrichment on top of the refresh, not part of it. A transient failure
        // must degrade to the pre-feature behaviour — metadata refreshed, relationships left as they
        // were — rather than failing a refresh that succeeded before this feature existed.
        try
        {
            await relationshipSyncService.SyncAsync(dataSourceId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Schema relationship sync failed for data source {DataSourceId}; metadata refresh kept.", dataSourceId);
        }

        return snapshot;
    }
}

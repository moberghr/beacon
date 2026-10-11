using Beacon.Core.Configuration;
using Beacon.Core.Models;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.DataSources;

internal sealed class RefreshDataSourceMetadataHandler(
    IDatabaseMetadataService metadataService,
    ISchemaRelationshipSyncService relationshipSyncService,
    ForcedMetadataRefreshCooldown cooldown,
    IOptionsMonitor<MetadataRefreshOptions> refreshOptions,
    ILogger<RefreshDataSourceMetadataHandler> logger)
    : IRequestHandler<RefreshDataSourceMetadataCommand, DatabaseMetadataSnapshot>
{
    public async Task<DatabaseMetadataSnapshot> Handle(RefreshDataSourceMetadataCommand request, CancellationToken cancellationToken)
    {
        if (request.DataSourceId <= 0)
        {
            throw new InvalidOperationException("Data source id must be positive.");
        }

        // One forced refresh per data source per cooldown, whoever asks: a refresh scans the live database. Only a refresh
        // that succeeded starts the cooldown, so a failed one can be retried at once.
        var cooldownMinutes = refreshOptions.CurrentValue.ForcedRefreshCooldownMinutes;
        var refusal = cooldown.TryStart(request.DataSourceId, cooldownMinutes);
        if (refusal != null)
        {
            throw new BeaconRateLimitedException(refusal);
        }

        DatabaseMetadataSnapshot snapshot;
        var succeeded = false;
        try
        {
            snapshot = await metadataService.RefreshMetadataAsync(request.DataSourceId, forceRefresh: true, cancellationToken);
            succeeded = true;
        }
        finally
        {
            cooldown.Finish(request.DataSourceId, cooldownMinutes, succeeded);
        }

        // Relationship sync is an enrichment on top of the refresh, not part of it. A transient failure
        // must degrade to the pre-feature behaviour — metadata refreshed, relationships left as they
        // were — rather than failing a refresh that succeeded before this feature existed.
        try
        {
            await relationshipSyncService.SyncAsync(request.DataSourceId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Schema relationship sync failed for data source {DataSourceId}; metadata refresh kept.", request.DataSourceId);
        }

        return snapshot;
    }
}

/// <summary>
/// Re-reads a data source's metadata from the database now. Curated descriptions are kept; a second forced refresh of the
/// same data source within <see cref="MetadataRefreshOptions.ForcedRefreshCooldownMinutes"/> of one that succeeded, or
/// while one is running, answers 429.
/// </summary>
public record RefreshDataSourceMetadataCommand(int DataSourceId) : IRequest<DatabaseMetadataSnapshot>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        ProjectCheck.SharedSource(ProjectResource.DataSource(DataSourceId), ProjectAction.Manage)
    ];
}

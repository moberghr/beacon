using Beacon.Core.Models.Metadata;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Handlers.DataSources;

internal sealed class RefreshDataSourceMetadataHandler(
    IDatabaseMetadataService metadataService,
    ISchemaRelationshipSyncService relationshipSyncService,
    ILogger<RefreshDataSourceMetadataHandler> logger)
    : IRequestHandler<RefreshDataSourceMetadataCommand, DatabaseMetadataSnapshot>
{
    public async Task<DatabaseMetadataSnapshot> Handle(RefreshDataSourceMetadataCommand request, CancellationToken cancellationToken)
    {
        if (request.DataSourceId <= 0)
        {
            throw new InvalidOperationException("Data source id must be positive.");
        }

        var snapshot = await metadataService.RefreshMetadataAsync(request.DataSourceId, forceRefresh: true, cancellationToken);

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

public record RefreshDataSourceMetadataCommand(int DataSourceId) : IRequest<DatabaseMetadataSnapshot>;

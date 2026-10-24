using Beacon.Core.Services;
using Warp.Core.Handlers;
using Warp.Core.Timeout;

namespace Beacon.SampleProject.Warp.Jobs;

// Re-scans a data source's metadata after its connection was pointed at another target, so the stored tables and columns
// describe the new target. Enqueued by the data-source update (IBeaconScheduler.EnqueueMetadataRefresh). A source archived,
// disabled or marked as a migration write target in the meantime is refused by the scan, which fails the job (Warp shows it).
[Timeout(3600, Mode = TimeoutMode.Fail)]
public sealed class RefreshDataSourceMetadataJob : IJob
{
    public int DataSourceId { get; init; }
}

public sealed class RefreshDataSourceMetadataJobHandler(IDatabaseMetadataService metadataService)
    : IJobHandler<RefreshDataSourceMetadataJob>
{
    public Task HandleAsync(RefreshDataSourceMetadataJob message, CancellationToken cancellationToken)
        => metadataService.RefreshMetadataAsync(message.DataSourceId, forceRefresh: true, cancellationToken);
}

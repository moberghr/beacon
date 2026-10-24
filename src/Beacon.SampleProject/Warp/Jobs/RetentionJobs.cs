using Beacon.Core.Services.Retention;
using Warp.Core.Concurrency;
using Warp.Core.Handlers;
using Warp.Core.Timeout;

namespace Beacon.SampleProject.Warp.Jobs;

// Data retention (Beacon:Retention): deletes the run history and logs older than their windows (none unless
// configured: 0 = keep forever) and clears content older than ContentDays. One run at a time, capped at an hour; a run
// that fails is investigated, not retried (§6.4), and the next day's run picks up what it left.
[Timeout(DataRetentionJob.TimeoutSeconds, Mode = TimeoutMode.Fail)]
[Mutex(DataRetentionJob.Name)]
public sealed class DataRetentionJob : IJob
{
    public const string Name = "data-retention";
    public const int TimeoutSeconds = 3600;
}

public sealed class DataRetentionJobHandler(IDataRetentionService retentionService) : IJobHandler<DataRetentionJob>
{
    public Task HandleAsync(DataRetentionJob message, CancellationToken cancellationToken)
        => retentionService.RunAsync(cancellationToken);
}

using Beacon.Core.BackgroundTasks;
using Warp.Core.Handlers;

namespace Beacon.SampleProject.Warp.Jobs;

// Runs a recorded background task (a background metadata refresh, ...), enqueued by IBeaconScheduler.EnqueueBackgroundTask.
// The user who started it travels as job metadata under "BeaconUserId", so JobStatusChangedBehavior pushes the job's state
// to that user only. A failed task fails the job too (Warp shows it; no retry).
public sealed class RunBackgroundTaskJob : IJob
{
    public int TaskId { get; init; }
}

public sealed class RunBackgroundTaskJobHandler(IBackgroundTaskService backgroundTasks)
    : IJobHandler<RunBackgroundTaskJob>
{
    public Task HandleAsync(RunBackgroundTaskJob message, CancellationToken cancellationToken)
        => backgroundTasks.ExecuteAsync(message.TaskId, cancellationToken);
}

using Beacon.Core.BackgroundTasks;
using Warp.Core.Handlers;
using Warp.Core.Timeout;

namespace Beacon.SampleProject.Warp.Jobs;

// Runs a recorded background task (a background metadata refresh, ...), enqueued by IBeaconScheduler.EnqueueBackgroundTask.
// The user who started it travels as job metadata under "BeaconUserId", so JobStatusChangedBehavior pushes the job's state
// to that user only. A failed task fails the job too (Warp shows it; no retry). The ceiling sits above the longest task
// (a migration run is stopped after its own TimeoutMinutes, at most 24 hours); the task row's claim prevents duplicates,
// so no mutex.
[Timeout(RunBackgroundTaskJob.TimeoutSeconds, Mode = TimeoutMode.Fail)]
public sealed class RunBackgroundTaskJob : IJob
{
    public const int TimeoutSeconds = 25 * 60 * 60;

    public int TaskId { get; init; }
}

public sealed class RunBackgroundTaskJobHandler(IBackgroundTaskService backgroundTasks)
    : IJobHandler<RunBackgroundTaskJob>
{
    public Task HandleAsync(RunBackgroundTaskJob message, CancellationToken cancellationToken)
        => backgroundTasks.ExecuteAsync(message.TaskId, cancellationToken);
}

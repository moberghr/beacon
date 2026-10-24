using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.BackgroundTasks;

/// <summary>
/// How an executor shows its running task is still going (<see cref="Data.Entities.BackgroundTask.HeartbeatTime"/>), so it
/// is given up as abandoned only <see cref="IBackgroundTaskExecutor.AbandonAfter"/> after its last sign of life, and learns
/// whether the task was given up meanwhile (then it stops before its next piece of work).
/// </summary>
public interface IBackgroundTaskHeartbeat
{
    /// <summary>Records that task <paramref name="taskId"/> is still going; false when it is no longer running.</summary>
    Task<bool> BeatAsync(int taskId, CancellationToken cancellationToken);
}

internal sealed class BackgroundTaskHeartbeat(IDbContextFactory<BeaconContext> contextFactory, TimeProvider timeProvider)
    : IBackgroundTaskHeartbeat
{
    public async Task<bool> BeatAsync(int taskId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var beaten = await context.BackgroundTasks
            .Where(x => x.Id == taskId)
            .Where(x => x.State == BackgroundTaskState.Running)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.HeartbeatTime, now), cancellationToken);

        return beaten == 1;
    }
}

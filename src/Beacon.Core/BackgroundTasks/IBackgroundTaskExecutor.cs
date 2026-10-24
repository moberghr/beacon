using Beacon.Core.Data.Enums;

namespace Beacon.Core.BackgroundTasks;

/// <summary>
/// Runs the tasks of one <see cref="BackgroundTaskKind"/>. Registered with <c>TryAddEnumerable</c>; one per kind. It runs in
/// the host's job, outside the request that started it: the project check already ran there, and work done for the user
/// who asked (<see cref="BackgroundTaskContext.RequestedByUserId"/>) sets that user as its run-as owner itself.
/// </summary>
public interface IBackgroundTaskExecutor
{
    BackgroundTaskKind Kind { get; }

    /// <summary>
    /// A task of this kind still queued or running this long after it was queued or started is given up as abandoned (its
    /// job runner stopped) when the next task on the same subject starts.
    /// </summary>
    TimeSpan AbandonAfter { get; }

    Task<BackgroundTaskOutcome> ExecuteAsync(BackgroundTaskContext task, CancellationToken cancellationToken);
}

/// <summary>The task an executor runs: its id, the id of the resource it works on, and the <c>Users.Id</c> of who asked.</summary>
public sealed record BackgroundTaskContext(int TaskId, int SubjectId, int? RequestedByUserId);

/// <summary>What a task that succeeded reports: how many items it left out or warned about, when its kind counts them.</summary>
public sealed record BackgroundTaskOutcome(int? WarningCount = null);

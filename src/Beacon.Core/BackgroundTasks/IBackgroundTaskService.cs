using Beacon.Core.Data.Enums;

namespace Beacon.Core.BackgroundTasks;

/// <summary>
/// Long work a caller starts and follows instead of waiting for it in the request: the start records a queued
/// <see cref="Data.Entities.BackgroundTask"/> and hands it to the host's job runner
/// (<see cref="Worker.IBeaconScheduler.EnqueueBackgroundTask"/>); the job runs it through the kind's
/// <see cref="IBackgroundTaskExecutor"/>; the caller reads its state through the route of the resource it works on, and the
/// host pushes <c>JobStatusChanged</c> for the job to the user who started it. One task of a kind per subject is queued or
/// running at a time. The caller's project check runs on the request that starts it and on the one that reads it.
/// </summary>
public interface IBackgroundTaskService
{
    /// <summary>
    /// Starts a task of <paramref name="kind"/> on <paramref name="subjectId"/>: null when one of that kind on that subject is
    /// already queued or running. A host whose job runner runs no background task gets it run in the request, and the
    /// ticket carries the state it finished in.
    /// </summary>
    Task<BackgroundTaskTicket?> StartAsync(BackgroundTaskKind kind, int subjectId, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a queued task: called by the host's job. A task that is no longer queued (finished, given up as abandoned, or
    /// taken by another run) is left alone. A failure is recorded with its code and thrown again, so the job fails too.
    /// </summary>
    Task ExecuteAsync(int taskId, CancellationToken cancellationToken);

    /// <summary>The task's state, or null when no task of <paramref name="kind"/> on <paramref name="subjectId"/> has that id.</summary>
    Task<BackgroundTaskStatus?> GetStatusAsync(BackgroundTaskKind kind, int subjectId, int taskId, CancellationToken cancellationToken);
}

/// <summary>
/// A started task: its id (to read its state), the host's job id (<c>JobStatusChanged</c> events carry it; null when it ran
/// in the request) and its state when the start answered.
/// </summary>
public sealed record BackgroundTaskTicket(int TaskId, string? JobId, BackgroundTaskState State);

/// <summary>
/// A task's state. <paramref name="FailureCode"/> says why a failed one failed (<see cref="BackgroundTaskFailureCodes"/>);
/// <paramref name="WarningCount"/> counts what it left out or warned about, when its kind reports that.
/// </summary>
public sealed record BackgroundTaskStatus(
    int TaskId,
    string? JobId,
    BackgroundTaskState State,
    DateTime CreatedTime,
    DateTime? StartedTime,
    DateTime? FinishedTime,
    string? FailureCode,
    int? WarningCount);

using Beacon.Core.Data.Enums;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.BackgroundTasks;

/// <summary>
/// Long work a caller starts and follows instead of waiting for it in the request: the start records a queued
/// <see cref="Data.Entities.BackgroundTask"/> and hands it to the host's job runner
/// (<see cref="Worker.IBeaconScheduler.EnqueueBackgroundTask"/>); the job runs it through the kind's
/// <see cref="IBackgroundTaskExecutor"/>; the caller reads its state through the route of the resource it works on, and the
/// host pushes <c>JobStatusChanged</c> for the job to the user who started it. One task of a kind per subject is queued or
/// running at a time. The caller's project check runs on the request that starts it and on the one that reads it; the job
/// runs as the user who started it and re-checks that user's access before the work (a refused task is skipped, never run
/// as the system).
/// </summary>
public interface IBackgroundTaskService
{
    /// <summary>
    /// Starts a task of <paramref name="kind"/> on <paramref name="subjectId"/>: null when one of that kind on that subject is
    /// already queued or running. <paramref name="auditEvent"/>, built from the new task's id, is saved with the task in one
    /// transaction before the task is handed to the job runner, so an accepted start is always on record and a refused one
    /// never is. A host whose job runner runs no background task gets it run in the request, and the ticket carries the
    /// state it finished in.
    /// </summary>
    Task<BackgroundTaskTicket?> StartAsync(
        BackgroundTaskKind kind,
        int subjectId,
        Func<int, SecurityAuditEntry>? auditEvent,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs a queued task: called by the host's job. A task that is no longer queued (finished, given up as abandoned, or
    /// taken by another run) is left alone. A failure is recorded with its code and thrown again, so the job fails too; a
    /// task that ran nothing (<see cref="BackgroundTaskOutcome.Skipped"/>, or the user who started it no longer exists) is
    /// recorded as skipped with its code, and the job succeeds.
    /// </summary>
    Task ExecuteAsync(int taskId, CancellationToken cancellationToken);

    /// <summary>
    /// The task's state, or null when no task of <paramref name="kind"/> on <paramref name="subjectId"/> has that id. An
    /// unfinished task of the kind on the subject that is past its limit is given up as abandoned first, so a task whose job
    /// was lost reads as failed instead of queued.
    /// </summary>
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

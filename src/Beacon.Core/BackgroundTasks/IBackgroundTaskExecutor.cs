using Beacon.Core.Data.Enums;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.BackgroundTasks;

/// <summary>
/// Runs the tasks of one <see cref="BackgroundTaskKind"/>. Registered with <c>TryAddEnumerable</c>; one per kind. In the
/// host's job, outside the request that started it, the scope runs as the user who asked
/// (<see cref="BackgroundTaskContext.RequestedByUserId"/>, set by the service before anything resolves the caller), so an
/// executor re-checks that user's access (<see cref="Authorization.IExecutionAccessCheck"/>) right before the work, and
/// answers <see cref="BackgroundTaskOutcome.Skipped"/> when it is gone.
/// </summary>
public interface IBackgroundTaskExecutor
{
    BackgroundTaskKind Kind { get; }

    /// <summary>
    /// A task of this kind still queued or running this long after it was queued, started or last showed it is going
    /// (<see cref="IBackgroundTaskHeartbeat"/>) is given up as abandoned (its job runner stopped) when the next task on the
    /// same subject starts or its state is read. An executor whose work can last longer bounds it below this.
    /// </summary>
    TimeSpan AbandonAfter { get; }

    Task<BackgroundTaskOutcome> ExecuteAsync(BackgroundTaskContext task, CancellationToken cancellationToken);

    /// <summary>
    /// The security event recorded when a task of this kind ran nothing (<paramref name="code"/>, a
    /// <see cref="BackgroundTaskFailureCodes"/> code), ids and codes only; null records none (the default, or when the work
    /// that refused it recorded its own).
    /// </summary>
    SecurityAuditEntry? SkippedEvent(BackgroundTaskContext task, string code) => null;
}

/// <summary>
/// An executor whose work keeps its own record of whether it is still going on (a migration run's history row). Before a
/// task of its kind starts on a subject, and when one's state is read, a running task of the subject is asked about: one
/// whose work has ended without the task saying so (its job runner stopped mid-run) is given up as abandoned then, instead
/// of holding the subject until <see cref="IBackgroundTaskExecutor.AbandonAfter"/>.
/// </summary>
public interface IReconcilingBackgroundTaskExecutor : IBackgroundTaskExecutor
{
    /// <summary>
    /// Whether the work of the running task on <paramref name="subjectId"/>, started at <paramref name="startedTime"/> and
    /// last showing it was going at <paramref name="heartbeatTime"/> (null: not yet), has ended (or never began and no
    /// longer will).
    /// </summary>
    Task<bool> HasEndedAsync(int subjectId, DateTime startedTime, DateTime? heartbeatTime, CancellationToken cancellationToken);
}

/// <summary>The task an executor runs: its id, the id of the resource it works on, and the <c>Users.Id</c> of who asked.</summary>
public sealed record BackgroundTaskContext(int TaskId, int SubjectId, int? RequestedByUserId);

/// <summary>
/// What a task that ran reports: how many items it left out or warned about, when its kind counts them; or, for a task that
/// ran nothing, why (<see cref="SkippedCode"/>, a <see cref="BackgroundTaskFailureCodes"/> code).
/// </summary>
public sealed record BackgroundTaskOutcome(int? WarningCount = null)
{
    public string? SkippedCode { get; private init; }

    /// <summary>The task ran nothing and is recorded as skipped with <paramref name="code"/>; its job does not fail.</summary>
    public static BackgroundTaskOutcome Skipped(string code) => new() { SkippedCode = code };
}

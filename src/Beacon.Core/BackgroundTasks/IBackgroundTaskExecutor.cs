using Beacon.Core.Data.Enums;

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
    /// A task of this kind still queued or running this long after it was queued or started is given up as abandoned (its
    /// job runner stopped) when the next task on the same subject starts.
    /// </summary>
    TimeSpan AbandonAfter { get; }

    Task<BackgroundTaskOutcome> ExecuteAsync(BackgroundTaskContext task, CancellationToken cancellationToken);
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

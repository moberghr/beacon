namespace Beacon.Core.Data.Enums;

/// <summary>
/// Where a <see cref="Entities.BackgroundTask"/> is: queued, running, or finished one way or the other. A skipped task ran
/// nothing: its failure code says why (the user who started it may no longer do it, or the same work was already running).
/// </summary>
public enum BackgroundTaskState
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Skipped = 4
}

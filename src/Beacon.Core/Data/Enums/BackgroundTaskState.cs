namespace Beacon.Core.Data.Enums;

/// <summary>Where a <see cref="Entities.BackgroundTask"/> is: queued, running, or finished one way or the other.</summary>
public enum BackgroundTaskState
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3
}

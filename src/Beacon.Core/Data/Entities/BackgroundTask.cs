using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities;

/// <summary>
/// Long work a caller started that runs as a background job (<see cref="Worker.IBeaconScheduler.EnqueueBackgroundTask"/>):
/// what it works on, who asked, and how far it got. Read through the route of the resource it works on, which runs that
/// resource's project check. One task of a kind per subject is queued or running at a time.
/// </summary>
public class BackgroundTask : BaseEntity
{
    public BackgroundTaskKind Kind { get; set; }

    /// <summary>The id of the resource the task works on; <see cref="Kind"/> says which kind of resource.</summary>
    public int SubjectId { get; set; }

    /// <summary>The <c>Users.Id</c> of the caller who started it; null when no stored user did.</summary>
    public int? RequestedByUserId { get; set; }

    public BackgroundTaskState State { get; set; }

    /// <summary>The host job runner's id for the job that runs it; null while it is not handed over, or when it ran in the request.</summary>
    public string? JobId { get; set; }

    public DateTime? StartedTime { get; set; }

    public DateTime? FinishedTime { get; set; }

    /// <summary>Why a failed task failed, as a code from <see cref="BackgroundTasks.BackgroundTaskFailureCodes"/>; never a message.</summary>
    public string? FailureCode { get; set; }

    /// <summary>How many items the task left out or warned about (for a metadata refresh, the names it did not store).</summary>
    public int? WarningCount { get; set; }
}

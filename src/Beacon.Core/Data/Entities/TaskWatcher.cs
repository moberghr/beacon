namespace Beacon.Core.Data.Entities;

/// <summary>
/// Tracks users watching a QueryTask. Users receive activity updates while watching.
/// Composite key (QueryTaskId, UserId). Watchers are removable directly — no soft delete.
/// </summary>
public class TaskWatcher
{
    public required int QueryTaskId { get; set; }
    public required string UserId { get; set; }
    public DateTime CreatedTime { get; set; } = DateTime.UtcNow;

    public QueryTask QueryTask { get; set; } = null!;
}

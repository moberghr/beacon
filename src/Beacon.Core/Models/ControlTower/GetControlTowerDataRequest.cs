using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;

namespace Beacon.Core.Models.ControlTower;

public record GetControlTowerDataRequest : ListRequest
{
    public int? DataSourceId { get; init; }
    public int? FolderId { get; init; }
    public HealthStatus? HealthStatus { get; init; }
    public bool? HasUnresolvedTasks { get; init; }
    public string? SearchKeyword { get; init; }

    /// <summary>
    /// Window for execution statistics, anomalies, and Stalled detection. Defaults to 30 days.
    /// </summary>
    public int TimeRangeDays { get; init; } = 30;
}

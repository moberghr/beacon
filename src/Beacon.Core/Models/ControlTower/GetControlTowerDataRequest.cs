using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;

namespace Beacon.Core.Models.ControlTower;

public class GetControlTowerDataRequest : BaseListRequest
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

    /// <summary>
    /// Sort column for the table. Defaults to WorstFirst (lowest success rate then most failures).
    /// </summary>
    public ControlTowerSortBy SortBy { get; init; } = ControlTowerSortBy.WorstFirst;
}

public enum ControlTowerSortBy
{
    WorstFirst = 0,
    Name = 1,
    SuccessRate = 2,
    Executions = 3,
    OpenTasks = 4,
    Anomalies = 5,
    LastExecution = 6
}

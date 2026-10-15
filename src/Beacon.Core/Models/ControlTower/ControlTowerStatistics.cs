namespace Beacon.Core.Models.ControlTower;

public record ControlTowerStatistics
{
    public int TotalSubscriptions { get; init; }
    public int HealthySubscriptions { get; init; }  // Green
    public int WarningSubscriptions { get; init; }  // Amber
    public int CriticalSubscriptions { get; init; } // Red
    public int StalledSubscriptions { get; init; }  // Stalled (no executions in window)
    public int TotalUnresolvedTasks { get; init; }
    public int TotalAnomalies30Days { get; init; }
    public double OverallSuccessRate { get; init; }
    public int TimeRangeDays { get; init; }

    /// <summary>
    /// The enabled data contracts the caller may read (on the filtered data source, when one is chosen) whose last
    /// scheduled evaluation was skipped because their owner may no longer run them; none has run since.
    /// </summary>
    public int OwnerNotAuthorizedContracts { get; init; }
}

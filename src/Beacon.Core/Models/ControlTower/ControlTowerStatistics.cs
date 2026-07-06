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
}

namespace Semantico.Core.Models.ControlTower;

public record ControlTowerStatistics
{
    public int TotalSubscriptions { get; init; }
    public int HealthySubscriptions { get; init; }  // Green
    public int WarningSubscriptions { get; init; }  // Amber
    public int CriticalSubscriptions { get; init; } // Red
    public int TotalUnresolvedTasks { get; init; }
    public int TotalAnomalies30Days { get; init; }
    public double OverallSuccessRate { get; init; }
}

namespace Beacon.Core.Models.Anomaly;

/// <summary>
/// Chart data for visualizing anomaly detection thresholds and results
/// </summary>
public class AnomalyChartData
{
    public List<AnomalyChartPoint> DataPoints { get; set; } = new();
    public decimal? BaselineMean { get; set; }
    public decimal? UpperThreshold { get; set; }
    public decimal? LowerThreshold { get; set; }
    public bool HasAnomalyDetection { get; set; }
    public string? DetectionMethod { get; set; }
    public string? Sensitivity { get; set; }

    /// <summary>
    /// Always null: the rolling baseline is no longer computed (nothing read it). Kept for compatibility.
    /// </summary>
    public List<decimal>? RollingBaseline { get; set; }

    /// <summary>
    /// Always null; see <see cref="RollingBaseline"/>.
    /// </summary>
    public List<decimal>? RollingUpperThreshold { get; set; }

    /// <summary>
    /// Always null; see <see cref="RollingBaseline"/>.
    /// </summary>
    public List<decimal>? RollingLowerThreshold { get; set; }
}

/// <summary>
/// Single data point in anomaly chart
/// </summary>
public class AnomalyChartPoint
{
    public DateTime DateTime { get; set; }
    public decimal ResultCount { get; set; }
    public bool IsAnomaly { get; set; }
    public bool NotificationSent { get; set; }
    public string? AnomalySeverity { get; set; }
    public int? QueryExecutionHistoryId { get; set; }
}

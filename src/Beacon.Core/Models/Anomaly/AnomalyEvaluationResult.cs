namespace Beacon.Core.Models.Anomaly;

/// <summary>
/// Result of anomaly evaluation, including statistical details
/// </summary>
public class AnomalyEvaluationResult
{
    public bool IsAnomaly { get; set; }
    public decimal CurrentValue { get; set; }
    public decimal? BaselineMean { get; set; }
    public decimal? BaselineStdDev { get; set; }
    public decimal? ZScore { get; set; }
    public string? Explanation { get; set; }
    public string Severity { get; set; } = "Medium";
    public int HistoricalDataPoints { get; set; }
}

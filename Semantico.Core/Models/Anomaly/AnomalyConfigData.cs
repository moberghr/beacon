using Semantico.Core.Data.Enums;

namespace Semantico.Core.Models.Anomaly;

/// <summary>
/// DTO for anomaly configuration
/// </summary>
public class AnomalyConfigData
{
    public int Id { get; set; }
    public int SubscriptionId { get; set; }
    public bool Enabled { get; set; }
    public AnomalyDetectionMethod DetectionMethod { get; set; }
    public AnomalySensitivity Sensitivity { get; set; }
    public int LookbackDays { get; set; }
    public bool AlertOnIncrease { get; set; }
    public bool AlertOnDecrease { get; set; }
    public int MinimumDataPoints { get; set; }
}

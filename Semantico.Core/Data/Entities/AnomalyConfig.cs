using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Data.Entities;

/// <summary>
/// Configuration for anomaly detection on a subscription.
/// Determines how baselines are calculated and when alerts are triggered.
/// </summary>
public class AnomalyConfig : BaseEntity
{
    /// <summary>
    /// The subscription this anomaly configuration belongs to
    /// </summary>
    public required int SubscriptionId { get; set; }

    /// <summary>
    /// Whether anomaly detection is enabled for this subscription
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Statistical method used for anomaly detection
    /// </summary>
    public AnomalyDetectionMethod DetectionMethod { get; set; } = AnomalyDetectionMethod.StandardDeviation;

    /// <summary>
    /// Sensitivity level (maps to threshold values internally)
    /// </summary>
    public AnomalySensitivity Sensitivity { get; set; } = AnomalySensitivity.Medium;

    /// <summary>
    /// Number of days of historical data to use for baseline calculation
    /// </summary>
    public int LookbackDays { get; set; } = 30;

    /// <summary>
    /// Alert when metric increases above baseline
    /// </summary>
    public bool AlertOnIncrease { get; set; } = true;

    /// <summary>
    /// Alert when metric decreases below baseline
    /// </summary>
    public bool AlertOnDecrease { get; set; } = true;

    /// <summary>
    /// Minimum number of historical data points required before anomaly detection is active
    /// </summary>
    public int MinimumDataPoints { get; set; } = 7;

    // Navigation properties
    public Subscription Subscription { get; set; } = null!;
}

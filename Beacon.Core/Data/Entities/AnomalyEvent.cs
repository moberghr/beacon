using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities;

/// <summary>
/// Record of detected anomalies with statistical details.
/// Used for tracking, reporting, and user acknowledgment.
/// </summary>
public class AnomalyEvent : BaseEntity
{
    /// <summary>
    /// The subscription that triggered this anomaly
    /// </summary>
    public required int SubscriptionId { get; set; }

    /// <summary>
    /// Related notification (if one was sent)
    /// </summary>
    public int? NotificationId { get; set; }

    /// <summary>
    /// When the anomaly was detected
    /// </summary>
    public required DateTime DetectedTime { get; set; }

    /// <summary>
    /// Current metric value that triggered the anomaly
    /// </summary>
    public required decimal CurrentValue { get; set; }

    /// <summary>
    /// Baseline mean from historical data
    /// </summary>
    public decimal? BaselineMean { get; set; }

    /// <summary>
    /// Baseline standard deviation from historical data
    /// </summary>
    public decimal? BaselineStdDev { get; set; }

    /// <summary>
    /// Z-score (number of standard deviations from mean)
    /// </summary>
    public decimal? ZScore { get; set; }

    /// <summary>
    /// Human-readable severity level (Low, Medium, High, Critical)
    /// </summary>
    public string Severity { get; set; } = "Medium";

    /// <summary>
    /// Human-readable explanation of the anomaly
    /// </summary>
    public string? Explanation { get; set; }

    /// <summary>
    /// Whether this anomaly has been acknowledged by a user
    /// </summary>
    public bool Acknowledged { get; set; } = false;

    /// <summary>
    /// Username of who acknowledged the anomaly
    /// </summary>
    public string? AcknowledgedBy { get; set; }

    // Navigation properties
    public Subscription Subscription { get; set; } = null!;
    public Notification? Notification { get; set; }
}

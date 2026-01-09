using Semantico.Core.Data.Entities.Base;

namespace Semantico.Core.Data.Entities;

/// <summary>
/// Historical metric values used to calculate anomaly baselines.
/// Stores row count from each query execution for trend analysis.
/// </summary>
public class AnomalyBaseline : BaseEntity
{
    /// <summary>
    /// The subscription this baseline belongs to
    /// </summary>
    public required int SubscriptionId { get; set; }

    /// <summary>
    /// When this metric was recorded (execution timestamp)
    /// </summary>
    public required DateTime ExecutionTime { get; set; }

    /// <summary>
    /// The metric value (row count for Phase 1)
    /// </summary>
    public required decimal MetricValue { get; set; }

    // Navigation properties
    public Subscription Subscription { get; set; } = null!;
}

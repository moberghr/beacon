namespace Beacon.Core.Data.Enums;

/// <summary>
/// Sensitivity level for anomaly detection (maps to threshold values)
/// </summary>
public enum AnomalySensitivity
{
    /// <summary>
    /// Low sensitivity - fewer alerts (3.0σ for StandardDeviation)
    /// </summary>
    Low = 1,

    /// <summary>
    /// Medium sensitivity - balanced (2.0σ for StandardDeviation)
    /// </summary>
    Medium = 2,

    /// <summary>
    /// High sensitivity - more alerts (1.5σ for StandardDeviation)
    /// </summary>
    High = 3
}

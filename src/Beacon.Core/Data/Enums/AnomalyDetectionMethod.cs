namespace Beacon.Core.Data.Enums;

/// <summary>
/// Statistical method used for anomaly detection
/// </summary>
public enum AnomalyDetectionMethod
{
    /// <summary>
    /// Standard deviation (Z-score): (current - mean) / std_dev
    /// Best for normally distributed data
    /// </summary>
    StandardDeviation = 1,

    /// <summary>
    /// Interquartile range: Q1 - 1.5*IQR, Q3 + 1.5*IQR
    /// Best for non-normal distributions, robust to outliers
    /// </summary>
    IQR = 2,

    /// <summary>
    /// Simple percentage change: (current - avg) / avg
    /// Easy to understand and configure
    /// </summary>
    PercentageChange = 3
}

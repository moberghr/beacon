using Semantico.Core.Models.Anomaly;

namespace Semantico.Core.Services;

public interface IAnomalyDetectionService
{
    /// <summary>
    /// Evaluates if the current query result represents an anomaly based on historical baselines
    /// </summary>
    Task<AnomalyEvaluationResult> EvaluateAnomalyAsync(
        int subscriptionId,
        int rowCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the current metric value as a baseline for future anomaly detection
    /// </summary>
    Task StoreBaselineAsync(
        int subscriptionId,
        decimal metricValue,
        DateTime executionTime,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves historical metrics for anomaly baseline calculation
    /// </summary>
    Task<List<decimal>> GetHistoricalMetricsAsync(
        int subscriptionId,
        int lookbackDays,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a detected anomaly event
    /// </summary>
    Task<int> RecordAnomalyEventAsync(
        int subscriptionId,
        AnomalyEvaluationResult evaluation,
        int? notificationId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets anomaly configuration for a subscription
    /// </summary>
    Task<AnomalyConfigData?> GetAnomalyConfigAsync(
        int subscriptionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates anomaly configuration for a subscription
    /// </summary>
    Task<int> SaveAnomalyConfigAsync(
        AnomalyConfigData config,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes anomaly configuration for a subscription
    /// </summary>
    Task DeleteAnomalyConfigAsync(
        int subscriptionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets chart data for visualizing anomaly thresholds and detection results
    /// </summary>
    Task<AnomalyChartData> GetAnomalyChartDataAsync(
        int subscriptionId,
        int lookbackDays = 30,
        CancellationToken cancellationToken = default);
}

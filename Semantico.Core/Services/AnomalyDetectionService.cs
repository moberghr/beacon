using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Anomaly;

namespace Semantico.Core.Services;

public class AnomalyDetectionService : IAnomalyDetectionService
{
    private readonly IDbContextFactory<SemanticoContext> _contextFactory;
    private readonly ILogger<AnomalyDetectionService> _logger;

    public AnomalyDetectionService(
        IDbContextFactory<SemanticoContext> contextFactory,
        ILogger<AnomalyDetectionService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task<AnomalyEvaluationResult> EvaluateAnomalyAsync(
        int subscriptionId,
        int rowCount,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var config = await context.AnomalyConfigs
            .FirstOrDefaultAsync(x => x.SubscriptionId == subscriptionId && x.Enabled, cancellationToken);

        if (config == null)
        {
            return new AnomalyEvaluationResult
            {
                IsAnomaly = false,
                CurrentValue = rowCount,
                Explanation = "Anomaly detection not enabled for this subscription"
            };
        }

        var historicalMetrics = await GetHistoricalMetricsAsync(
            subscriptionId,
            config.LookbackDays,
            cancellationToken);

        if (historicalMetrics.Count < config.MinimumDataPoints)
        {
            return new AnomalyEvaluationResult
            {
                IsAnomaly = false,
                CurrentValue = rowCount,
                Explanation = $"Not enough historical data (need {config.MinimumDataPoints}, have {historicalMetrics.Count}). Building baseline...",
                HistoricalDataPoints = historicalMetrics.Count
            };
        }

        return config.DetectionMethod switch
        {
            AnomalyDetectionMethod.StandardDeviation => EvaluateStandardDeviation(rowCount, historicalMetrics, config),
            AnomalyDetectionMethod.IQR => EvaluateIQR(rowCount, historicalMetrics, config),
            AnomalyDetectionMethod.PercentageChange => EvaluatePercentageChange(rowCount, historicalMetrics, config),
            _ => throw new ArgumentException($"Unknown detection method: {config.DetectionMethod}")
        };
    }

    public async Task StoreBaselineAsync(
        int subscriptionId,
        decimal metricValue,
        DateTime executionTime,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var baseline = new AnomalyBaseline
        {
            SubscriptionId = subscriptionId,
            ExecutionTime = executionTime,
            MetricValue = metricValue
        };

        context.AnomalyBaselines.Add(baseline);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<decimal>> GetHistoricalMetricsAsync(
        int subscriptionId,
        int lookbackDays,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var cutoffDate = DateTime.UtcNow.AddDays(-lookbackDays);

        return await context.AnomalyBaselines
            .Where(x => x.SubscriptionId == subscriptionId && x.ExecutionTime >= cutoffDate)
            .OrderBy(x => x.ExecutionTime)
            .Select(x => x.MetricValue)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> RecordAnomalyEventAsync(
        int subscriptionId,
        AnomalyEvaluationResult evaluation,
        int? notificationId = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var anomalyEvent = new AnomalyEvent
        {
            SubscriptionId = subscriptionId,
            NotificationId = notificationId,
            DetectedTime = DateTime.UtcNow,
            CurrentValue = evaluation.CurrentValue,
            BaselineMean = evaluation.BaselineMean,
            BaselineStdDev = evaluation.BaselineStdDev,
            ZScore = evaluation.ZScore,
            Severity = evaluation.Severity,
            Explanation = evaluation.Explanation
        };

        context.AnomalyEvents.Add(anomalyEvent);
        await context.SaveChangesAsync(cancellationToken);

        return anomalyEvent.Id;
    }

    public async Task<AnomalyConfigData?> GetAnomalyConfigAsync(
        int subscriptionId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.AnomalyConfigs
            .Where(x => x.SubscriptionId == subscriptionId)
            .Select(x => new AnomalyConfigData
            {
                Id = x.Id,
                SubscriptionId = x.SubscriptionId,
                Enabled = x.Enabled,
                DetectionMethod = x.DetectionMethod,
                Sensitivity = x.Sensitivity,
                LookbackDays = x.LookbackDays,
                AlertOnIncrease = x.AlertOnIncrease,
                AlertOnDecrease = x.AlertOnDecrease,
                MinimumDataPoints = x.MinimumDataPoints
            })
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<int> SaveAnomalyConfigAsync(
        AnomalyConfigData config,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await context.AnomalyConfigs
            .FirstOrDefaultAsync(x => x.SubscriptionId == config.SubscriptionId, cancellationToken);

        if (existing != null)
        {
            // Update existing configuration
            existing.Enabled = config.Enabled;
            existing.DetectionMethod = config.DetectionMethod;
            existing.Sensitivity = config.Sensitivity;
            existing.LookbackDays = config.LookbackDays;
            existing.AlertOnIncrease = config.AlertOnIncrease;
            existing.AlertOnDecrease = config.AlertOnDecrease;
            existing.MinimumDataPoints = config.MinimumDataPoints;

            context.AnomalyConfigs.Update(existing);
        }
        else
        {
            // Create new configuration
            var newConfig = new AnomalyConfig
            {
                SubscriptionId = config.SubscriptionId,
                Enabled = config.Enabled,
                DetectionMethod = config.DetectionMethod,
                Sensitivity = config.Sensitivity,
                LookbackDays = config.LookbackDays,
                AlertOnIncrease = config.AlertOnIncrease,
                AlertOnDecrease = config.AlertOnDecrease,
                MinimumDataPoints = config.MinimumDataPoints
            };

            context.AnomalyConfigs.Add(newConfig);
        }

        await context.SaveChangesAsync(cancellationToken);

        return existing?.Id ?? (await context.AnomalyConfigs
            .Where(x => x.SubscriptionId == config.SubscriptionId)
            .Select(x => x.Id)
            .FirstAsync(cancellationToken));
    }

    public async Task DeleteAnomalyConfigAsync(
        int subscriptionId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var config = await context.AnomalyConfigs
            .FirstOrDefaultAsync(x => x.SubscriptionId == subscriptionId, cancellationToken);

        if (config != null)
        {
            context.AnomalyConfigs.Remove(config);
            await context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Deleted anomaly configuration for subscription {SubscriptionId}", subscriptionId);
        }
    }

    public async Task<AnomalyChartData> GetAnomalyChartDataAsync(
        int subscriptionId,
        int lookbackDays = 30,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var config = await context.AnomalyConfigs
            .FirstOrDefaultAsync(x => x.SubscriptionId == subscriptionId && x.Enabled, cancellationToken);

        if (config == null)
        {
            // No anomaly detection configured - return empty chart data
            return new AnomalyChartData
            {
                HasAnomalyDetection = false
            };
        }

        var cutoffDate = DateTime.UtcNow.AddDays(-lookbackDays);

        // Get query execution history with result counts
        var executionHistory = await context.QueryExecutionHistory
            .Where(x => x.SubscriptionId == subscriptionId && x.CreatedTime >= cutoffDate)
            .OrderBy(x => x.CreatedTime)
            .Select(x => new
            {
                x.Id,
                x.CreatedTime,
                x.ResultCount,
                x.NotificationStatus
            })
            .ToListAsync(cancellationToken);

        // Get anomaly events in the same period
        var anomalyEvents = await context.AnomalyEvents
            .Where(x => x.SubscriptionId == subscriptionId && x.DetectedTime >= cutoffDate)
            .OrderBy(x => x.DetectedTime)
            .Select(x => new
            {
                x.DetectedTime,
                x.CurrentValue,
                x.Severity
            })
            .ToListAsync(cancellationToken);

        // Get historical metrics for baseline calculation
        var historicalMetrics = await GetHistoricalMetricsAsync(subscriptionId, config.LookbackDays, cancellationToken);

        // Calculate baseline and thresholds
        decimal? baselineMean = null;
        decimal? upperThreshold = null;
        decimal? lowerThreshold = null;

        if (historicalMetrics.Count >= config.MinimumDataPoints)
        {
            baselineMean = historicalMetrics.Average();

            // Calculate thresholds based on detection method
            switch (config.DetectionMethod)
            {
                case AnomalyDetectionMethod.StandardDeviation:
                    var variance = historicalMetrics.Sum(x => (x - baselineMean.Value) * (x - baselineMean.Value)) / historicalMetrics.Count;
                    var stdDev = (decimal)Math.Sqrt((double)variance);
                    var threshold = GetThresholdForSensitivity(config.Sensitivity);

                    if (config.AlertOnIncrease)
                        upperThreshold = baselineMean + (threshold * stdDev);
                    if (config.AlertOnDecrease)
                        lowerThreshold = baselineMean - (threshold * stdDev);
                    break;

                case AnomalyDetectionMethod.IQR:
                    var sorted = historicalMetrics.OrderBy(x => x).ToList();
                    var q1Index = (int)(sorted.Count * 0.25);
                    var q3Index = (int)(sorted.Count * 0.75);
                    var q1 = sorted[q1Index];
                    var q3 = sorted[q3Index];
                    var iqr = q3 - q1;

                    if (config.AlertOnIncrease)
                        upperThreshold = q3 + 1.5m * iqr;
                    if (config.AlertOnDecrease)
                        lowerThreshold = q1 - 1.5m * iqr;
                    break;

                case AnomalyDetectionMethod.PercentageChange:
                    var percentThreshold = GetPercentageThresholdForSensitivity(config.Sensitivity);

                    if (config.AlertOnIncrease)
                        upperThreshold = baselineMean * (1 + percentThreshold);
                    if (config.AlertOnDecrease)
                        lowerThreshold = baselineMean * (1 - percentThreshold);
                    break;
            }
        }

        // Build data points and calculate rolling baseline
        var dataPoints = new List<AnomalyChartPoint>();
        var rollingBaseline = new List<decimal>();
        var rollingUpperThreshold = new List<decimal>();
        var rollingLowerThreshold = new List<decimal>();

        // Get all baseline data for rolling calculation
        var allBaselines = await context.AnomalyBaselines
            .Where(x => x.SubscriptionId == subscriptionId)
            .OrderBy(x => x.ExecutionTime)
            .Select(x => new { x.ExecutionTime, x.MetricValue })
            .ToListAsync(cancellationToken);

        foreach (var execution in executionHistory)
        {
            var anomalyEvent = anomalyEvents.FirstOrDefault(x =>
                Math.Abs((x.DetectedTime - execution.CreatedTime).TotalMinutes) < 1);

            dataPoints.Add(new AnomalyChartPoint
            {
                DateTime = execution.CreatedTime,
                ResultCount = execution.ResultCount,
                IsAnomaly = anomalyEvent != null,
                NotificationSent = execution.NotificationStatus == NotificationStatus.NotificationSent,
                AnomalySeverity = anomalyEvent?.Severity,
                QueryExecutionHistoryId = execution.Id
            });

            // Calculate rolling baseline for this point using historical data up to this point
            var historicalCutoff = execution.CreatedTime.AddDays(-config.LookbackDays);
            var rollingHistoricalData = allBaselines
                .Where(x => x.ExecutionTime < execution.CreatedTime && x.ExecutionTime >= historicalCutoff)
                .Select(x => x.MetricValue)
                .ToList();

            if (rollingHistoricalData.Count >= config.MinimumDataPoints)
            {
                var rollingMean = rollingHistoricalData.Average();
                rollingBaseline.Add(rollingMean);

                // Calculate rolling thresholds based on detection method
                switch (config.DetectionMethod)
                {
                    case AnomalyDetectionMethod.StandardDeviation:
                        var rollingVariance = rollingHistoricalData.Sum(x => (x - rollingMean) * (x - rollingMean)) / rollingHistoricalData.Count;
                        var rollingStdDev = (decimal)Math.Sqrt((double)rollingVariance);
                        var threshold = GetThresholdForSensitivity(config.Sensitivity);

                        if (config.AlertOnIncrease)
                            rollingUpperThreshold.Add(rollingMean + (threshold * rollingStdDev));
                        else
                            rollingUpperThreshold.Add(rollingMean); // Just add mean as placeholder

                        if (config.AlertOnDecrease)
                            rollingLowerThreshold.Add(rollingMean - (threshold * rollingStdDev));
                        else
                            rollingLowerThreshold.Add(rollingMean); // Just add mean as placeholder
                        break;

                    case AnomalyDetectionMethod.IQR:
                        var sorted = rollingHistoricalData.OrderBy(x => x).ToList();
                        var q1Index = (int)(sorted.Count * 0.25);
                        var q3Index = (int)(sorted.Count * 0.75);
                        var q1 = sorted[q1Index];
                        var q3 = sorted[q3Index];
                        var iqr = q3 - q1;

                        if (config.AlertOnIncrease)
                            rollingUpperThreshold.Add(q3 + 1.5m * iqr);
                        else
                            rollingUpperThreshold.Add(rollingMean);

                        if (config.AlertOnDecrease)
                            rollingLowerThreshold.Add(q1 - 1.5m * iqr);
                        else
                            rollingLowerThreshold.Add(rollingMean);
                        break;

                    case AnomalyDetectionMethod.PercentageChange:
                        var percentThreshold = GetPercentageThresholdForSensitivity(config.Sensitivity);

                        if (config.AlertOnIncrease)
                            rollingUpperThreshold.Add(rollingMean * (1 + percentThreshold));
                        else
                            rollingUpperThreshold.Add(rollingMean);

                        if (config.AlertOnDecrease)
                            rollingLowerThreshold.Add(rollingMean * (1 - percentThreshold));
                        else
                            rollingLowerThreshold.Add(rollingMean);
                        break;
                }
            }
            else
            {
                // Not enough data points yet - use current value or zero
                rollingBaseline.Add(execution.ResultCount);
                rollingUpperThreshold.Add(execution.ResultCount);
                rollingLowerThreshold.Add(execution.ResultCount);
            }
        }

        return new AnomalyChartData
        {
            DataPoints = dataPoints,
            BaselineMean = baselineMean,
            UpperThreshold = upperThreshold,
            LowerThreshold = lowerThreshold,
            RollingBaseline = rollingBaseline.Any() ? rollingBaseline : null,
            RollingUpperThreshold = rollingUpperThreshold.Any() && config.AlertOnIncrease ? rollingUpperThreshold : null,
            RollingLowerThreshold = rollingLowerThreshold.Any() && config.AlertOnDecrease ? rollingLowerThreshold : null,
            HasAnomalyDetection = true,
            DetectionMethod = config.DetectionMethod.ToString(),
            Sensitivity = config.Sensitivity.ToString()
        };
    }

    private AnomalyEvaluationResult EvaluateStandardDeviation(
        decimal currentValue,
        List<decimal> historicalMetrics,
        AnomalyConfig config)
    {
        var mean = historicalMetrics.Average();
        var variance = historicalMetrics.Sum(x => (x - mean) * (x - mean)) / historicalMetrics.Count;
        var stdDev = (decimal)Math.Sqrt((double)variance);

        var zScore = stdDev == 0 ? 0 : (currentValue - mean) / stdDev;
        var threshold = GetThresholdForSensitivity(config.Sensitivity);

        var isAnomaly = Math.Abs(zScore) > threshold;
        var isIncrease = zScore > 0;
        var isDecrease = zScore < 0;

        // Check alert direction settings
        if (isAnomaly)
        {
            if (isIncrease && !config.AlertOnIncrease)
                isAnomaly = false;
            if (isDecrease && !config.AlertOnDecrease)
                isAnomaly = false;
        }

        var severity = CalculateSeverity(Math.Abs(zScore));
        var explanation = GenerateStandardDeviationExplanation(currentValue, mean, stdDev, zScore, isAnomaly);

        return new AnomalyEvaluationResult
        {
            IsAnomaly = isAnomaly,
            CurrentValue = currentValue,
            BaselineMean = mean,
            BaselineStdDev = stdDev,
            ZScore = zScore,
            Severity = severity,
            Explanation = explanation,
            HistoricalDataPoints = historicalMetrics.Count
        };
    }

    private AnomalyEvaluationResult EvaluateIQR(
        decimal currentValue,
        List<decimal> historicalMetrics,
        AnomalyConfig config)
    {
        var sorted = historicalMetrics.OrderBy(x => x).ToList();
        var q1Index = (int)(sorted.Count * 0.25);
        var q3Index = (int)(sorted.Count * 0.75);

        var q1 = sorted[q1Index];
        var q3 = sorted[q3Index];
        var iqr = q3 - q1;

        var lowerBound = q1 - 1.5m * iqr;
        var upperBound = q3 + 1.5m * iqr;

        var isAnomaly = currentValue < lowerBound || currentValue > upperBound;
        var isIncrease = currentValue > upperBound;
        var isDecrease = currentValue < lowerBound;

        // Check alert direction settings
        if (isAnomaly)
        {
            if (isIncrease && !config.AlertOnIncrease)
                isAnomaly = false;
            if (isDecrease && !config.AlertOnDecrease)
                isAnomaly = false;
        }

        var mean = historicalMetrics.Average();
        var severity = CalculateSeverityIQR(currentValue, lowerBound, upperBound, iqr);
        var explanation = GenerateIQRExplanation(currentValue, q1, q3, lowerBound, upperBound, isAnomaly);

        return new AnomalyEvaluationResult
        {
            IsAnomaly = isAnomaly,
            CurrentValue = currentValue,
            BaselineMean = mean,
            Severity = severity,
            Explanation = explanation,
            HistoricalDataPoints = historicalMetrics.Count
        };
    }

    private AnomalyEvaluationResult EvaluatePercentageChange(
        decimal currentValue,
        List<decimal> historicalMetrics,
        AnomalyConfig config)
    {
        var baseline = historicalMetrics.Average();

        if (baseline == 0)
        {
            return new AnomalyEvaluationResult
            {
                IsAnomaly = currentValue != 0,
                CurrentValue = currentValue,
                BaselineMean = baseline,
                Severity = currentValue != 0 ? "High" : "Low",
                Explanation = currentValue != 0
                    ? $"Current: {currentValue:N0}, Baseline: 0 (infinite change)"
                    : "Current: 0, Baseline: 0 (no change)",
                HistoricalDataPoints = historicalMetrics.Count
            };
        }

        var percentChange = Math.Abs((currentValue - baseline) / baseline);
        var thresholdPercent = GetPercentageThresholdForSensitivity(config.Sensitivity);

        var isAnomaly = percentChange > thresholdPercent;
        var isIncrease = currentValue > baseline;
        var isDecrease = currentValue < baseline;

        // Check alert direction settings
        if (isAnomaly)
        {
            if (isIncrease && !config.AlertOnIncrease)
                isAnomaly = false;
            if (isDecrease && !config.AlertOnDecrease)
                isAnomaly = false;
        }

        var severity = CalculateSeverityPercentage(percentChange);
        var explanation = GeneratePercentageExplanation(currentValue, baseline, percentChange, isAnomaly);

        return new AnomalyEvaluationResult
        {
            IsAnomaly = isAnomaly,
            CurrentValue = currentValue,
            BaselineMean = baseline,
            Severity = severity,
            Explanation = explanation,
            HistoricalDataPoints = historicalMetrics.Count
        };
    }

    private decimal GetThresholdForSensitivity(AnomalySensitivity sensitivity)
    {
        return sensitivity switch
        {
            AnomalySensitivity.High => 1.5m,    // More alerts
            AnomalySensitivity.Medium => 2.0m,   // Balanced
            AnomalySensitivity.Low => 3.0m,      // Fewer alerts
            _ => 2.0m
        };
    }

    private decimal GetPercentageThresholdForSensitivity(AnomalySensitivity sensitivity)
    {
        return sensitivity switch
        {
            AnomalySensitivity.High => 0.15m,    // 15% change
            AnomalySensitivity.Medium => 0.25m,   // 25% change
            AnomalySensitivity.Low => 0.40m,      // 40% change
            _ => 0.25m
        };
    }

    private string CalculateSeverity(decimal absZScore)
    {
        return absZScore switch
        {
            >= 4.0m => "Critical",
            >= 3.0m => "High",
            >= 2.0m => "Medium",
            _ => "Low"
        };
    }

    private string CalculateSeverityIQR(decimal current, decimal lower, decimal upper, decimal iqr)
    {
        var deviationMultiple = current < lower
            ? (lower - current) / (iqr == 0 ? 1 : iqr)
            : (current - upper) / (iqr == 0 ? 1 : iqr);

        return deviationMultiple switch
        {
            >= 3.0m => "Critical",
            >= 2.0m => "High",
            >= 1.0m => "Medium",
            _ => "Low"
        };
    }

    private string CalculateSeverityPercentage(decimal percentChange)
    {
        return percentChange switch
        {
            >= 1.0m => "Critical",  // 100%+ change
            >= 0.5m => "High",       // 50%+ change
            >= 0.25m => "Medium",    // 25%+ change
            _ => "Low"
        };
    }

    private string GenerateStandardDeviationExplanation(
        decimal current,
        decimal mean,
        decimal stdDev,
        decimal zScore,
        bool isAnomaly)
    {
        var direction = zScore > 0 ? "above" : "below";
        var status = isAnomaly ? "ANOMALY DETECTED" : "Within normal range";

        return $"{status}: Current: {current:N0}, Expected: {mean:N0}±{stdDev:N0}, " +
               $"Z-Score: {Math.Abs(zScore):N2}σ {direction} baseline";
    }

    private string GenerateIQRExplanation(
        decimal current,
        decimal q1,
        decimal q3,
        decimal lower,
        decimal upper,
        bool isAnomaly)
    {
        var status = isAnomaly ? "ANOMALY DETECTED" : "Within normal range";
        return $"{status}: Current: {current:N0}, Expected range: {lower:N0} to {upper:N0} " +
               $"(Q1: {q1:N0}, Q3: {q3:N0})";
    }

    private string GeneratePercentageExplanation(
        decimal current,
        decimal baseline,
        decimal percentChange,
        bool isAnomaly)
    {
        var direction = current > baseline ? "increase" : "decrease";
        var status = isAnomaly ? "ANOMALY DETECTED" : "Within normal range";

        return $"{status}: Current: {current:N0}, Baseline: {baseline:N0}, " +
               $"{percentChange:P0} {direction}";
    }
}

---
title: Anomaly Detection
description: Statistical anomaly detection that learns baselines from historical execution data and flags unusual result-count patterns.
---

Statistical anomaly detection that learns from historical subscription execution data and alerts on unusual patterns.

## Purpose

Anomaly detection in Beacon provides:
- **Automatic Baseline Learning**: System learns "normal" patterns from historical data
- **Statistical Detection**: Multiple methods (Z-score, IQR, percentage change) to detect anomalies
- **Real-time Alerting**: Immediate notifications when anomalies are detected
- **False Positive Reduction**: Configurable thresholds and minimum data requirements

## How It Works

### Overview

1. **Baseline Building**: Every subscription execution stores row count in `AnomalyBaseline` table
2. **Historical Analysis**: System accumulates data over configured lookback period
3. **Anomaly Evaluation**: When configured minimum data points is reached, detection activates
4. **Alert Generation**: If anomaly detected, creates alert task or sends notification

### Baseline Learning

**Automatic Data Collection:**
- Every time a subscription executes, row count is stored
- No manual configuration required
- Baseline continuously refines as more data is collected

**Requirements:**
- Minimum data points (default: 30) must be collected before detection activates
- Data older than lookback period (default: 30 days) is excluded
- At least one execution per day recommended for daily detection

## Detection Methods

### 1. Standard Deviation (Z-Score)

**Best for:** Normally distributed data, general-purpose anomaly detection

**How it works:**
1. Calculate mean (μ) and standard deviation (σ) from historical data
2. Compute Z-score: `z = (x - μ) / σ`
3. Flag as anomaly if `|z| > threshold` (typically 2-3 standard deviations)

**Example:**
```
Historical data: [10, 12, 11, 13, 10, 12, 11]
Mean: 11.29
Std Dev: 1.03
Threshold: 2.5

Current value: 18
Z-score: (18 - 11.29) / 1.03 = 6.51
Result: ANOMALY (6.51 > 2.5)
```

**Configuration:**
```json
{
  "DetectionMethod": "StandardDeviation",
  "Threshold": 2.5,  // Number of standard deviations
  "LookbackDays": 30,
  "MinimumDataPoints": 30
}
```

**Pros:**
- Intuitive and well-understood
- Works well for normally distributed data
- Adjustable sensitivity via threshold

**Cons:**
- Sensitive to outliers in historical data
- Assumes normal distribution
- May have high false positives for skewed data

### 2. Interquartile Range (IQR)

**Best for:** Skewed distributions, outlier detection, data with extreme values

**How it works:**
1. Calculate Q1 (25th percentile) and Q3 (75th percentile)
2. Calculate IQR: `IQR = Q3 - Q1`
3. Define bounds: Lower = Q1 - 1.5×IQR, Upper = Q3 + 1.5×IQR
4. Flag as anomaly if value < Lower or value > Upper

**Example:**
```
Historical data (sorted): [8, 10, 11, 12, 13, 14, 16, 20]
Q1 (25%): 10.5
Q3 (75%): 15
IQR: 15 - 10.5 = 4.5

Lower bound: 10.5 - (1.5 × 4.5) = 3.75
Upper bound: 15 + (1.5 × 4.5) = 21.75

Current value: 35
Result: ANOMALY (35 > 21.75)
```

**Configuration:**
```json
{
  "DetectionMethod": "IQR",
  "Threshold": 1.5,  // IQR multiplier
  "LookbackDays": 30,
  "MinimumDataPoints": 30
}
```

**Pros:**
- Robust to outliers
- No assumption of normal distribution
- Good for skewed data

**Cons:**
- Less sensitive to subtle changes
- Requires more data points for accuracy
- May miss gradual shifts

### 3. Percentage Change

**Best for:** Trend monitoring, business metrics, detecting sudden spikes/drops

**How it works:**
1. Get most recent historical value
2. Calculate percentage change: `((current - previous) / previous) × 100`
3. Flag as anomaly if `|percentage_change| > threshold`

**Example:**
```
Previous value: 100
Current value: 165
Percentage change: ((165 - 100) / 100) × 100 = 65%
Threshold: 50%

Result: ANOMALY (65% > 50%)
```

**Configuration:**
```json
{
  "DetectionMethod": "PercentageChange",
  "Threshold": 50,  // Percentage threshold
  "LookbackDays": 7,  // Recent comparison window
  "MinimumDataPoints": 7
}
```

**Pros:**
- Intuitive for business users
- Detects sudden changes quickly
- Works well for trending metrics

**Cons:**
- Sensitive to daily/weekly patterns
- May give false positives during expected spikes
- Doesn't consider historical variance

## Configuration

### Subscription-Level Configuration

Each subscription can have its own anomaly detection configuration:

**Entity:** `AnomalyConfig`

| Property | Type | Description | Default |
|----------|------|-------------|---------|
| `Enabled` | bool | Enable/disable anomaly detection | false |
| `DetectionMethod` | enum | StandardDeviation, IQR, PercentageChange | StandardDeviation |
| `Threshold` | decimal | Sensitivity threshold (meaning varies by method) | 2.5 |
| `LookbackDays` | int | How many days of history to analyze | 30 |
| `MinimumDataPoints` | int | Min historical executions before detection activates | 30 |

### Example Configurations

#### Daily Error Monitoring
```csharp
new AnomalyConfig
{
    SubscriptionId = 1,
    Enabled = true,
    DetectionMethod = AnomalyDetectionMethod.StandardDeviation,
    Threshold = 2.5m,  // 2.5 std deviations
    LookbackDays = 30,
    MinimumDataPoints = 30
}
```

#### Sales Spike Detection
```csharp
new AnomalyConfig
{
    SubscriptionId = 2,
    Enabled = true,
    DetectionMethod = AnomalyDetectionMethod.PercentageChange,
    Threshold = 50m,  // 50% increase/decrease
    LookbackDays = 7,
    MinimumDataPoints = 7
}
```

#### Outlier Detection for Skewed Data
```csharp
new AnomalyConfig
{
    SubscriptionId = 3,
    Enabled = true,
    DetectionMethod = AnomalyDetectionMethod.IQR,
    Threshold = 1.5m,  // Standard IQR multiplier
    LookbackDays = 60,
    MinimumDataPoints = 50
}
```

## Usage Example

### Via Service

```csharp
public class AnomalyDetectionExample
{
    private readonly IAnomalyDetectionService _anomalyService;
    private readonly IDbContextFactory<BeaconContext> _contextFactory;

    public async Task ConfigureAnomalyDetectionAsync(int subscriptionId)
    {
        using var context = await _contextFactory.CreateDbContextAsync();

        // Create or update configuration
        var config = await context.AnomalyConfigs
            .FirstOrDefaultAsync(x => x.SubscriptionId == subscriptionId)
            ?? new AnomalyConfig { SubscriptionId = subscriptionId };

        config.Enabled = true;
        config.DetectionMethod = AnomalyDetectionMethod.StandardDeviation;
        config.Threshold = 2.5m;
        config.LookbackDays = 30;
        config.MinimumDataPoints = 30;

        if (config.Id == 0)
            context.AnomalyConfigs.Add(config);

        await context.SaveChangesAsync();
    }

    public async Task EvaluateExecutionAsync(int subscriptionId, int rowCount)
    {
        // Store baseline
        await _anomalyService.StoreBaselineAsync(
            subscriptionId,
            rowCount,
            DateTime.UtcNow);

        // Evaluate for anomaly
        var result = await _anomalyService.EvaluateAnomalyAsync(
            subscriptionId,
            rowCount);

        if (result.IsAnomaly)
        {
            Console.WriteLine($"ANOMALY DETECTED!");
            Console.WriteLine($"Current: {result.CurrentValue}");
            Console.WriteLine($"Mean: {result.Mean}");
            Console.WriteLine($"Std Dev: {result.StandardDeviation}");
            Console.WriteLine($"Explanation: {result.Explanation}");

            // Create alert task or send notification
        }
        else
        {
            Console.WriteLine($"Normal execution: {rowCount} rows");
        }
    }
}
```

### Integration with Subscriptions

Anomaly detection is automatically integrated into subscription execution:

```csharp
public class SubscriptionExecutionFlow
{
    // Simplified flow
    public async Task ExecuteAsync(Subscription subscription)
    {
        // 1. Execute query
        var result = await ExecuteQueryAsync(subscription.Query);
        int rowCount = result.Rows.Count;

        // 2. Store baseline (always, regardless of config)
        await _anomalyService.StoreBaselineAsync(
            subscription.Id,
            rowCount,
            DateTime.UtcNow);

        // 3. Evaluate for anomaly (only if configured)
        var anomalyResult = await _anomalyService.EvaluateAnomalyAsync(
            subscription.Id,
            rowCount);

        // 4. If anomaly detected
        if (anomalyResult.IsAnomaly)
        {
            // Option A: Create alert task
            await _taskService.CreateAnomalyTaskAsync(
                subscription,
                anomalyResult);

            // Option B: Send notification
            await _notificationService.SendAnomalyAlertAsync(
                subscription,
                anomalyResult);
        }

        // 5. Continue with normal notification flow
        if (rowCount > 0)
        {
            await _notificationService.SendAsync(subscription, result);
        }
    }
}
```

## Evaluation Result

### AnomalyEvaluationResult Model

```csharp
public class AnomalyEvaluationResult
{
    public bool IsAnomaly { get; set; }
    public decimal CurrentValue { get; set; }
    public decimal? Mean { get; set; }
    public decimal? StandardDeviation { get; set; }
    public decimal? PercentageChange { get; set; }
    public string Explanation { get; set; }
    public int HistoricalDataPoints { get; set; }
}
```

### Example Results

**Normal Execution:**
```json
{
  "IsAnomaly": false,
  "CurrentValue": 12,
  "Mean": 11.5,
  "StandardDeviation": 1.2,
  "Explanation": "Value within expected range (Z-score: 0.42)",
  "HistoricalDataPoints": 45
}
```

**Anomaly Detected:**
```json
{
  "IsAnomaly": true,
  "CurrentValue": 85,
  "Mean": 12.3,
  "StandardDeviation": 2.1,
  "Explanation": "Value significantly above historical mean (Z-score: 34.62). This is 34.62 standard deviations above normal.",
  "HistoricalDataPoints": 45
}
```

**Insufficient Data:**
```json
{
  "IsAnomaly": false,
  "CurrentValue": 15,
  "Explanation": "Not enough historical data (need 30, have 5). Building baseline...",
  "HistoricalDataPoints": 5
}
```

## Use Cases

### 1. Error Rate Monitoring

**Scenario:** Monitor application error rates

**Query:**
```sql
SELECT COUNT(*) as error_count
FROM logs
WHERE level = 'ERROR'
  AND created_at > NOW() - INTERVAL '1 hour';
```

**Configuration:**
- Method: StandardDeviation
- Threshold: 3.0 (very sensitive)
- Lookback: 7 days
- Min Data Points: 168 (hourly for 7 days)

**Result:** Alerts when error count exceeds 3 standard deviations above normal hourly rate

### 2. Sales Performance Tracking

**Scenario:** Detect unusual sales patterns (both spikes and drops)

**Query:**
```sql
SELECT COUNT(*) as daily_orders
FROM orders
WHERE DATE(created_at) = CURRENT_DATE;
```

**Configuration:**
- Method: PercentageChange
- Threshold: 30% (alert on 30% deviation)
- Lookback: 30 days
- Min Data Points: 30

**Result:** Alerts when daily orders are 30%+ different from previous day

### 3. System Resource Monitoring

**Scenario:** Monitor database connection pool usage

**Query:**
```sql
SELECT COUNT(*) as active_connections
FROM pg_stat_activity
WHERE state = 'active';
```

**Configuration:**
- Method: IQR
- Threshold: 1.5 (standard outlier detection)
- Lookback: 90 days
- Min Data Points: 90

**Result:** Alerts when connection count is an outlier compared to historical distribution

### 4. Data Quality Monitoring

**Scenario:** Detect sudden increases in null values

**Query:**
```sql
SELECT COUNT(*) as null_count
FROM products
WHERE description IS NULL OR price IS NULL;
```

**Configuration:**
- Method: StandardDeviation
- Threshold: 2.0
- Lookback: 14 days
- Min Data Points: 14

**Result:** Alerts when null count deviates from normal pattern

## Choosing the Right Method

| Scenario | Best Method | Why |
|----------|-------------|-----|
| Consistent daily metrics | StandardDeviation | Good for stable patterns |
| Sales, revenue, traffic | PercentageChange | Business users understand percentages |
| Highly variable data | IQR | Robust to outliers |
| Error rates, system metrics | StandardDeviation | Sensitive to subtle changes |
| Detecting spikes/crashes | PercentageChange | Quick reaction to sudden changes |
| Data with seasonal patterns | IQR | Less affected by regular variations |

## Tuning Recommendations

### Threshold Sensitivity

**StandardDeviation:**
- `2.0` - Very sensitive (more false positives)
- `2.5` - Balanced (recommended start)
- `3.0` - Conservative (fewer false positives)

**IQR:**
- `1.5` - Standard outlier detection
- `2.0` - More conservative
- `3.0` - Only extreme outliers

**PercentageChange:**
- `20%` - Very sensitive
- `50%` - Balanced
- `100%` - Only major changes

### Lookback Period

**Short (7-14 days):**
- Pros: Adapts quickly to recent changes
- Cons: Sensitive to weekly patterns
- Best for: Fast-changing environments

**Medium (30 days):**
- Pros: Balanced, captures monthly patterns
- Cons: Slower to adapt to new normals
- Best for: General-purpose monitoring

**Long (60-90 days):**
- Pros: Stable baseline, seasonal awareness
- Cons: May miss gradual shifts
- Best for: Seasonal business metrics

### Minimum Data Points

**Low (7-14 points):**
- Pros: Detects anomalies faster
- Cons: Higher false positive rate
- Best for: New subscriptions, urgent monitoring

**Medium (30 points):**
- Pros: Good balance of speed and accuracy
- Cons: 30-day wait before detection
- Best for: Most use cases

**High (60-90 points):**
- Pros: Very stable baseline
- Cons: Long baseline building period
- Best for: Critical systems, low false positive tolerance

## Troubleshooting

### Issue: "Not enough historical data"

**Symptom:** Anomaly detection never activates

**Causes:**
1. Subscription hasn't executed enough times
2. `MinimumDataPoints` set too high
3. Subscription executions failing

**Solutions:**
1. Wait for more executions to accumulate
2. Lower `MinimumDataPoints` (carefully)
3. Check subscription execution history for failures

### Issue: Too many false positives

**Symptom:** Anomaly alerts on normal variations

**Causes:**
1. Threshold too sensitive
2. Data has natural high variance
3. Wrong detection method for data pattern

**Solutions:**
1. Increase threshold (e.g., 2.5 → 3.0 for Z-score)
2. Try IQR method (more robust)
3. Increase lookback period for more context

### Issue: Missing real anomalies

**Symptom:** Known anomalies not detected

**Causes:**
1. Threshold too conservative
2. Insufficient historical data
3. Anomaly within "normal" range after drift

**Solutions:**
1. Decrease threshold
2. Ensure adequate baseline data
3. Consider PercentageChange for sudden spikes

## Database Schema

### AnomalyConfig Entity

```sql
CREATE TABLE anomaly_configs (
    id SERIAL PRIMARY KEY,
    subscription_id INT NOT NULL REFERENCES subscriptions(id),
    enabled BOOLEAN NOT NULL DEFAULT false,
    detection_method VARCHAR(50) NOT NULL,
    threshold DECIMAL(10,2) NOT NULL,
    lookback_days INT NOT NULL DEFAULT 30,
    minimum_data_points INT NOT NULL DEFAULT 30,
    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMP NOT NULL DEFAULT NOW()
);
```

### AnomalyBaseline Entity

```sql
CREATE TABLE anomaly_baselines (
    id SERIAL PRIMARY KEY,
    subscription_id INT NOT NULL REFERENCES subscriptions(id),
    execution_time TIMESTAMP NOT NULL,
    metric_value DECIMAL(18,2) NOT NULL,
    created_at TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_anomaly_baselines_subscription_time
    ON anomaly_baselines(subscription_id, execution_time DESC);
```

## API Reference

### IAnomalyDetectionService

```csharp
public interface IAnomalyDetectionService
{
    /// <summary>
    /// Evaluates current execution for anomalies based on historical baseline
    /// </summary>
    Task<AnomalyEvaluationResult> EvaluateAnomalyAsync(
        int subscriptionId,
        int rowCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores execution result for future baseline calculations
    /// </summary>
    Task StoreBaselineAsync(
        int subscriptionId,
        decimal metricValue,
        DateTime executionTime,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets historical metrics for analysis
    /// </summary>
    Task<List<decimal>> GetHistoricalMetricsAsync(
        int subscriptionId,
        int lookbackDays,
        CancellationToken cancellationToken = default);
}
```

## Related Features

- [Subscriptions](/features/subscriptions/) - Configure scheduled queries with anomaly detection
- [AI Integration](/features/ai-integration/) - Complement statistical detection with AI-powered insights

:::note
When an anomaly is detected, Beacon can raise an alerting task. Alerting tasks are auto-created from subscriptions (when enabled), auto-resolve when a query returns 0 rows, and support trend charts, comments, and manual resolution in the UI (`/tasks`).
:::

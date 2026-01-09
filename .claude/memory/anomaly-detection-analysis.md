# Anomaly Detection Feature Analysis for Semantico

## Executive Summary

**Verdict: HIGHLY VIABLE** ✅

Semantico is exceptionally well-positioned to add anomaly detection. The existing infrastructure (query execution, scheduling, notifications, task management) provides 80% of what's needed. Anomaly detection would be a natural evolution that reduces alert fatigue and provides intelligent monitoring.

---

## Current Semantico Capabilities

### Core Architecture
- **Query Engine**: Multi-step SQL execution with result chaining
- **Scheduling**: Cron-based subscription execution
- **Notifications**: Multi-channel (Email, Slack, Teams, Jira)
- **Task Management**: Auto-creation with lifecycle tracking
- **History Storage**: Full execution history with results
- **Multi-Database**: PostgreSQL, SQL Server, MySQL support

### Existing Patterns
```
Subscription → Query Execution → Store Results → Send Notification
                                      ↓
                              Task Creation (if enabled)
```

---

## Five Pillars of Data Observability (Monte Carlo)

| Pillar | Definition | Semantico Current Support |
|--------|------------|---------------------------|
| **Freshness** | Data currency and update gaps | ✅ Can detect with queries |
| **Distribution** | Field-level data health within ranges | ✅ Can detect with queries |
| **Volume** | Meeting expected data intake thresholds | ✅ Can detect with queries |
| **Schema** | Structural changes to data systems | ⚠️ Not directly supported |
| **Lineage** | Upstream/downstream dependencies | ⚠️ Multi-step queries provide partial support |

**Current Gap**: Users must manually define thresholds and write SQL to detect anomalies. No automatic baseline learning.

---

## Proposed Anomaly Detection Architecture

### Enhanced Flow
```
Subscription → Query Execution → Store Results
                                      ↓
                              Anomaly Evaluator
                              ├─ Load Historical Baseline
                              ├─ Calculate Current Metrics
                              ├─ Apply Detection Function
                              └─ Determine if Anomaly
                                      ↓
                              ┌────────┴────────┐
                        Anomaly              Normal
                           ↓                    ↓
                  Send Notification      Silent (no alert)
                  Create/Update Task
```

### Key Components to Add

#### 1. Anomaly Configuration Entity
```csharp
public class AnomalyConfig
{
    public int Id { get; set; }
    public int SubscriptionId { get; set; }

    // Detection settings
    public AnomalyDetectionMethod Method { get; set; } // ZScore, IQR, PercentageChange
    public int LookbackDays { get; set; } // 7, 14, 30, 90
    public decimal Threshold { get; set; } // 2.0 (std devs), 0.20 (20% change)

    // What to monitor
    public string MetricColumn { get; set; } // Which column to track
    public AnomalyMetricType MetricType { get; set; } // RowCount, Sum, Average, Max, Min

    // Alerting behavior
    public bool AlertOnIncrease { get; set; }
    public bool AlertOnDecrease { get; set; }
    public int MinimumDataPoints { get; set; } // Don't alert until N historical points
}

public enum AnomalyDetectionMethod
{
    StandardDeviation,  // Z-score: (current - mean) / std_dev
    IQR,               // Interquartile range: Q1 - 1.5*IQR, Q3 + 1.5*IQR
    PercentageChange,  // Simple: (current - avg) / avg
    MovingAverage      // Compare to moving average
}

public enum AnomalyMetricType
{
    RowCount,          // Total rows returned
    Sum,               // SUM(column)
    Average,           // AVG(column)
    Max,               // MAX(column)
    Min,               // MIN(column)
    NullRate,          // COUNT(NULL) / COUNT(*)
    DistinctCount      // COUNT(DISTINCT column)
}
```

#### 2. Baseline Storage
```csharp
public class AnomalyBaseline
{
    public int Id { get; set; }
    public int SubscriptionId { get; set; }
    public DateTime Timestamp { get; set; }
    public decimal MetricValue { get; set; }
    public string MetricName { get; set; }
}
```

#### 3. Anomaly Detection Service
```csharp
public interface IAnomalyDetectionService
{
    Task<AnomalyEvaluationResult> EvaluateAnomalyAsync(
        int subscriptionId,
        QueryResult currentResult);

    Task StoreBaselineAsync(int subscriptionId, QueryResult result);

    Task<List<decimal>> GetHistoricalMetricsAsync(
        int subscriptionId,
        int lookbackDays);
}

public class AnomalyEvaluationResult
{
    public bool IsAnomaly { get; set; }
    public decimal CurrentValue { get; set; }
    public decimal BaselineMean { get; set; }
    public decimal BaselineStdDev { get; set; }
    public decimal ZScore { get; set; }
    public string Explanation { get; set; } // "Current: 150, Expected: 100±20, 2.5σ above baseline"
}
```

---

## Statistical Methods

### 1. Standard Deviation (Z-Score)
**Best for**: Normally distributed data, detecting outliers

```csharp
decimal CalculateZScore(decimal current, List<decimal> historical)
{
    var mean = historical.Average();
    var stdDev = CalculateStdDev(historical);
    return (current - mean) / stdDev;
}

bool IsAnomaly(decimal zScore, decimal threshold)
{
    return Math.Abs(zScore) > threshold; // e.g., threshold = 2.0
}
```

**Example**: Daily order count is 150, but average is 100 with std dev of 20
- Z-score = (150 - 100) / 20 = 2.5
- If threshold = 2.0, this IS an anomaly

### 2. Interquartile Range (IQR)
**Best for**: Non-normal distributions, robust to outliers

```csharp
bool IsAnomalyIQR(decimal current, List<decimal> historical)
{
    var sorted = historical.OrderBy(x => x).ToList();
    var q1 = sorted[(int)(sorted.Count * 0.25)];
    var q3 = sorted[(int)(sorted.Count * 0.75)];
    var iqr = q3 - q1;

    var lowerBound = q1 - 1.5m * iqr;
    var upperBound = q3 + 1.5m * iqr;

    return current < lowerBound || current > upperBound;
}
```

### 3. Percentage Change
**Best for**: Simple detection, easy to understand

```csharp
bool IsAnomalyPercentage(decimal current, List<decimal> historical, decimal threshold)
{
    var baseline = historical.Average();
    var percentChange = Math.Abs((current - baseline) / baseline);
    return percentChange > threshold; // e.g., threshold = 0.20 (20%)
}
```

---

## Implementation Phases

### Phase 1: MVP - Row Count Anomaly Detection
**Goal**: Detect when query returns unusually high/low row counts

**Features**:
- Enable/disable anomaly detection on subscription
- Simple configuration: sensitivity (high/medium/low)
- Automatic baseline calculation from last 30 executions
- Alert only when anomaly detected
- Show baseline info in notification

**UI Changes**:
```razor
<MudSwitch @bind-Checked="subscription.EnableAnomalyDetection">
    Enable Anomaly Detection
</MudSwitch>

@if (subscription.EnableAnomalyDetection)
{
    <MudSelect @bind-Value="subscription.AnomalySensitivity">
        <MudSelectItem Value="High">High (1.5σ)</MudSelectItem>
        <MudSelectItem Value="Medium">Medium (2.0σ)</MudSelectItem>
        <MudSelectItem Value="Low">Low (3.0σ)</MudSelectItem>
    </MudSelect>

    <MudNumericField @bind-Value="subscription.AnomalyLookbackDays"
                     Label="Lookback Period (days)" />
}
```

**Notification Enhancement**:
```
🔴 ANOMALY DETECTED

Query: Daily Orders Count
Current: 150 rows
Baseline: 100±20 rows (last 30 days)
Z-Score: 2.5σ (High deviation)

This is 50% above the expected range.
```

### Phase 2: Distribution Anomaly Detection
**Features**:
- Monitor specific numeric columns (SUM, AVG, MAX, MIN)
- Null rate detection
- Multiple metrics per subscription

**Configuration**:
```csharp
subscription.AnomalyMetrics.Add(new AnomalyMetric {
    ColumnName = "revenue",
    MetricType = AnomalyMetricType.Sum,
    Method = AnomalyDetectionMethod.StandardDeviation,
    Threshold = 2.0m
});
```

### Phase 3: Advanced Features
- **Seasonality Detection**: Account for day-of-week, time-of-day patterns
- **Trend Detection**: Distinguish anomalies from gradual trends
- **Correlation**: Detect when multiple metrics anomaly together
- **ML Models**: Use ML.NET for advanced pattern detection
- **Automatic Tuning**: Learn optimal thresholds over time

---

## Use Cases

### 1. E-Commerce Platform
**Scenario**: Monitor daily order volume

**Traditional Approach** (current):
```sql
SELECT COUNT(*) as order_count
FROM orders
WHERE DATE(created_at) = CURRENT_DATE
```
- Sends notification EVERY day
- User must eyeball if 150 orders is normal
- Alert fatigue sets in

**With Anomaly Detection**:
- Same query, anomaly detection enabled
- System learns baseline: 100±20 orders/day
- Only alerts when count is <60 or >140
- Notification shows: "150 orders today (↑50%, 2.5σ above normal)"

### 2. API Monitoring
**Scenario**: Track error rates

**Query**:
```sql
SELECT COUNT(*) as error_count
FROM error_logs
WHERE severity = 'ERROR'
  AND created_at > NOW() - INTERVAL '1 hour'
```

**Anomaly Config**:
- Method: Standard Deviation
- Threshold: 2.0σ
- Lookback: 7 days
- Alert on increase only

**Outcome**: Only alerts when error count spikes above expected range

### 3. Data Quality
**Scenario**: Monitor null rates in critical fields

**Query**:
```sql
SELECT
    COUNT(*) as total_rows,
    COUNT(*) FILTER (WHERE email IS NULL) as null_emails,
    CAST(COUNT(*) FILTER (WHERE email IS NULL) AS DECIMAL) / COUNT(*) as null_rate
FROM users
WHERE created_at > NOW() - INTERVAL '1 day'
```

**Anomaly Detection**: Monitor `null_rate` column
- Baseline: 0.01 (1% null rate)
- Current: 0.15 (15% null rate)
- Alert: "Null rate anomaly: 15× expected"

### 4. Data Freshness
**Scenario**: Ensure daily ETL completed

**Query**:
```sql
SELECT MAX(updated_at) as last_update,
       EXTRACT(EPOCH FROM (NOW() - MAX(updated_at))) / 3600 as hours_since_update
FROM sales_fact
```

**Anomaly Detection**: Monitor `hours_since_update`
- Baseline: 1-2 hours (normal ETL lag)
- Current: 12 hours
- Alert: "Data freshness anomaly: 10× longer than expected"

---

## Competitive Analysis

### Monte Carlo Data
- **Pricing**: $50k-$200k/year
- **Features**: Full data observability platform
- **Gap**: Enterprise-only, expensive

### Datadog
- **Pricing**: Per-metric pricing
- **Features**: APM + monitoring with anomaly detection
- **Gap**: Not database-focused

### Semantico + Anomaly Detection
- **Pricing**: Open source / self-hosted
- **Features**: Database monitoring with intelligent anomaly detection
- **Advantage**: Affordable, database-native, SQL-based

---

## Benefits

### For Users
✅ **Reduce Alert Fatigue**: Only get notified when something is actually wrong
✅ **No Manual Thresholds**: System learns baselines automatically
✅ **Intelligent Monitoring**: Detect issues users didn't know to look for
✅ **Better Insights**: Understand "is this normal?" automatically

### For Semantico Product
✅ **Competitive Differentiation**: Most monitoring tools lack anomaly detection
✅ **Natural Evolution**: Builds on existing infrastructure
✅ **Premium Feature**: Could be paid tier for hosted version
✅ **Market Positioning**: Move from "monitoring" to "observability"

---

## Challenges & Solutions

### Challenge 1: Statistical Complexity
**Risk**: Users don't understand Z-scores, IQR, etc.

**Solution**:
- Simple presets: High/Medium/Low sensitivity
- Clear explanations in UI: "High = alert if 1.5× above normal"
- Visual feedback: Show baseline range on charts

### Challenge 2: Cold Start Problem
**Risk**: Need historical data before anomaly detection works

**Solution**:
- Minimum data points requirement (e.g., 7 days)
- Show "Learning baseline..." status
- Allow manual baseline seeding

### Challenge 3: False Positives
**Risk**: Too many false anomaly alerts

**Solution**:
- Tunable thresholds per subscription
- "Snooze" feature for known anomalies
- Learn from user feedback (mark as false positive)

### Challenge 4: Performance
**Risk**: Calculating baselines on every execution is slow

**Solution**:
- Pre-calculate baselines periodically (hourly/daily)
- Cache baseline statistics
- Use efficient SQL queries for historical data

### Challenge 5: Storage Growth
**Risk**: Storing all historical metrics increases DB size

**Solution**:
- Retention policy (90 days default)
- Aggregate old data (daily → weekly → monthly)
- Optional: Only store anomalies long-term

---

## Database Schema Changes

```sql
-- Anomaly configuration
CREATE TABLE anomaly_configs (
    id SERIAL PRIMARY KEY,
    subscription_id INT NOT NULL REFERENCES subscriptions(id),
    enabled BOOLEAN DEFAULT FALSE,
    detection_method VARCHAR(50) NOT NULL, -- 'StandardDeviation', 'IQR', etc.
    lookback_days INT DEFAULT 30,
    threshold DECIMAL(10,2) DEFAULT 2.0,
    metric_column VARCHAR(255), -- NULL = row count
    metric_type VARCHAR(50) DEFAULT 'RowCount',
    alert_on_increase BOOLEAN DEFAULT TRUE,
    alert_on_decrease BOOLEAN DEFAULT TRUE,
    minimum_data_points INT DEFAULT 7,
    created_time TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- Historical baselines
CREATE TABLE anomaly_baselines (
    id SERIAL PRIMARY KEY,
    subscription_id INT NOT NULL REFERENCES subscriptions(id),
    execution_time TIMESTAMP NOT NULL,
    metric_name VARCHAR(255) NOT NULL,
    metric_value DECIMAL(18,4) NOT NULL,
    metadata JSONB, -- Store additional context
    INDEX idx_subscription_time (subscription_id, execution_time DESC)
);

-- Anomaly events (detected anomalies)
CREATE TABLE anomaly_events (
    id SERIAL PRIMARY KEY,
    subscription_id INT NOT NULL REFERENCES subscriptions(id),
    notification_id INT REFERENCES notifications(id),
    detected_time TIMESTAMP NOT NULL,
    current_value DECIMAL(18,4) NOT NULL,
    baseline_mean DECIMAL(18,4),
    baseline_std_dev DECIMAL(18,4),
    z_score DECIMAL(10,4),
    severity VARCHAR(20), -- 'Low', 'Medium', 'High'
    acknowledged BOOLEAN DEFAULT FALSE,
    acknowledged_by VARCHAR(255),
    acknowledged_time TIMESTAMP
);
```

---

## MVP Development Estimate

### Phase 1 Tasks (Row Count Anomaly Detection)
1. Database migrations for anomaly tables (2 hours)
2. AnomalyConfig entity and service (4 hours)
3. AnomalyDetectionService implementation (8 hours)
4. Integrate into SubscriptionService execution flow (4 hours)
5. UI for enabling/configuring anomaly detection (4 hours)
6. Enhanced notification messages with baseline info (3 hours)
7. Unit tests (4 hours)
8. Documentation (3 hours)

**Total: ~32 hours** (4-5 days)

### Phase 2 Tasks (Distribution Anomaly)
1. Multi-metric configuration (6 hours)
2. Column-specific metric extraction (6 hours)
3. UI for multiple metrics (4 hours)
4. Testing (4 hours)

**Total: ~20 hours** (2-3 days)

---

## Recommendation

### Proceed with MVP Implementation ✅

**Reasons**:
1. **High User Value**: Dramatically reduces alert fatigue
2. **Low Implementation Cost**: ~5 days for MVP
3. **Strong Market Fit**: Fills gap between basic monitoring and expensive observability platforms
4. **Natural Evolution**: Builds on existing solid foundation
5. **Competitive Advantage**: Differentiates Semantico from alternatives

### Suggested Approach
1. **Start with Phase 1 MVP**: Row count anomaly detection only
2. **Beta Test**: Get user feedback on sensitivity, false positives
3. **Iterate**: Tune algorithms based on real usage
4. **Expand to Phase 2**: Add distribution anomaly once validated

### Success Metrics
- Reduction in notification volume (target: 70% fewer alerts)
- User-reported false positive rate (target: <10%)
- Detection accuracy for known issues (target: >90%)
- User adoption rate (target: 50% of subscriptions enable it)

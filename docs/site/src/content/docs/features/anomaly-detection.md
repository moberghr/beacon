---
title: Anomaly Detection
description: Statistical detection on a subscription's row count. Beacon learns a baseline from past runs and notifies only when a run deviates from it.
---

Anomaly detection changes when a [subscription](/features/subscriptions/) notifies. Instead of its notification trigger,
Beacon compares each run's **row count** with the row counts of the subscription's earlier runs and sends the
notification only when the run is unusual.

It is statistical (standard deviation, interquartile range or percentage change), part of `Beacon.Core`, and needs no
LLM.

:::note[The metric is the row count]
Beacon measures the number of rows a run returns, not a value inside the result. A query such as `SELECT COUNT(*) FROM …`
always returns one row, so its row count never changes. Write the query to return one row per item you want to count,
for example `SELECT id FROM orders WHERE created_at >= CURRENT_DATE`.
:::

## Turn it on

Anomaly detection is set when you create the subscription; a subscription cannot be edited afterwards.

1. In **Subscriptions** (`/beacon/subscriptions`), click **New subscription**.
2. On the **Anomaly** step, tick **Enable anomaly detection**.
3. Set the fields below and finish the subscription.

| Field | Default | Dialog accepts | What it does |
|-------|---------|-------|--------------|
| **Detection method** | Standard deviation | Standard deviation, IQR, Percentage change | How a run is compared with the baseline. See [Methods](#methods). |
| **Sensitivity** | Medium | Low, Medium, High | The threshold of the method. High alerts more often. IQR ignores it. |
| **Lookback (days)** | 30 | 7–365 | Age of the baseline runs: only runs from the last N days count. |
| **Minimum data points** | 7 | 3–100 | Baseline runs needed before any run can be an anomaly. |
| **Alert on an unusual increase** | on | | Alert when the row count is unusually high. |
| **Alert on an unusual decrease** | on | | Alert when the row count is unusually low. The dialog requires at least one of the two. |

The ranges and the either/or rule are the dialog's. Through the API, the same settings go in the `anomalyConfig`
object of `POST /beacon/api/subscriptions` (`detectionMethod`, `sensitivity`, `lookbackDays`, `minimumDataPoints`,
`alertOnIncrease`, `alertOnDecrease`), and the API stores the values it is given.

## How a run is evaluated

For a subscription with anomaly detection on, each run that executes its query goes through these steps:

1. **Evaluate.** Beacon loads the stored baseline values of the subscription from the last **Lookback** days. With fewer
   than **Minimum data points** values, the run is not an anomaly ("Not enough historical data … Building baseline…").
   Otherwise the configured method decides.
2. **Decide the notification.** The usual checks come first: a run that timed out is `Timeout`, a run with 0 rows is
   `NoResults`, and a run below the subscription's minimum row count is `BelowThreshold`. After those, an anomaly sends
   the notification (`NotificationSent`) and any other run is silenced (`NotificationSilenced`). The subscription's
   notification trigger is not used while anomaly detection is on. A run whose query returns no rows ends as
   `NoResults`; anomaly detection compares runs that return rows. The [Control Tower](/features/control-tower/) shows
   `NoResults` runs.
3. **Store the baseline.** The run's row count is stored as a new baseline value, with the time of the run. A run is not
   stored when all of its recipients wait for approval of their destination, so nobody could be told about it.
4. **Record the event.** For an anomaly, Beacon records an anomaly event (current value, baseline mean, standard deviation,
   z-score, severity, explanation), linked to the first notification that was delivered, if any. Recipients receive the
   evaluation with the notification; the webhook payload carries it as `Anomaly`.

Baselines are stored only while anomaly detection is on for the subscription. Runs that did not execute the query
(skipped because the owner may no longer run it, or because the query's change is not approved) store nothing.

[Tasks](/features/tasks/) are independent of anomaly detection: they follow the **Create a task** option of the
subscription and open on any run with rows.

## Methods

All three methods use the baseline values of the lookback window, oldest to newest, without the current run.

### Standard deviation

Beacon computes the mean (μ) and the population standard deviation (σ) of the baseline, then the z-score of the run:
`z = (x − μ) / σ`. The run is an anomaly when `|z|` is above the sensitivity's threshold and its direction (above or
below the mean) has alerting turned on.

| Sensitivity | Threshold |
|-------------|-----------|
| High | 1.5σ |
| Medium | 2σ |
| Low | 3σ |

Severity by `|z|`: Critical from 4, High from 3, Medium from 2, otherwise Low. When every baseline value is the same
(σ = 0), the z-score is 0 and no run is an anomaly.

```text
Baseline:  [10, 12, 11, 13, 10, 12, 11]   mean 11.29, σ 1.03
Run:       18 rows                         z = (18 − 11.29) / 1.03 = 6.5
Medium:    6.5 > 2  → anomaly, severity Critical
```

Suits steady data.

### IQR (interquartile range)

Beacon sorts the baseline and takes Q1 and Q3 (the values at 25% and 75% of the sorted list), then
`IQR = Q3 − Q1`. The run is an anomaly when it is below `Q1 − 1.5 × IQR` (with decrease alerts on) or above
`Q3 + 1.5 × IQR` (with increase alerts on). The multiplier is fixed at 1.5; **Sensitivity** does not change it.

Severity by how many IQRs the run lies beyond the bound: Critical from 3, High from 2, Medium from 1, otherwise Low.

```text
Baseline (sorted): [8, 10, 11, 12, 13, 14, 16, 20]   Q1 11, Q3 16, IQR 5
Bounds:            11 − 7.5 = 3.5   to   16 + 7.5 = 23.5
Run:               35 rows  → above 23.5 → anomaly
```

Suits history with occasional spikes.

### Percentage change

Beacon compares the run with the **mean of the baseline** (not with the previous run):
`change = |x − mean| / mean`. The run is an anomaly when the change is above the sensitivity's threshold and its
direction has alerting turned on.

| Sensitivity | Threshold |
|-------------|-----------|
| High | 15% |
| Medium | 25% |
| Low | 40% |

Severity by change: Critical from 100%, High from 50%, Medium from 25%, otherwise Low. When the baseline mean is 0, any
run with rows is an anomaly (severity High).

```text
Baseline mean: 100
Run:           140 rows  → change 40%
Medium:        40% > 25% → anomaly, severity Medium
```

Suits business volumes where a relative change is what matters.

### Choosing a method

| Data | Method |
|------|--------|
| Steady counts with little variation | Standard deviation |
| Counts with occasional extreme values | IQR |
| Volumes where a relative change matters (orders, sign-ups) | Percentage change |

## Tuning

| Problem | What to change |
|---------|----------------|
| Alerts on normal variation | Lower the sensitivity (Low), try IQR, or lengthen the lookback. |
| Real changes go unnoticed | Raise the sensitivity (High), or use Percentage change for sudden jumps. |
| Detection never starts | The subscription has fewer baseline runs in the lookback window than **Minimum data points**. Wait for more runs, or create the subscription again with a lower minimum or a longer lookback. |
| Only increases (or only decreases) matter | Turn off the other direction. |

The lookback window counts days, not runs. A subscription that runs hourly with a 7-day lookback has up to 168 baseline
values; one that runs daily has 7. Set **Minimum data points** to what the schedule can deliver within the lookback.

## Where you see it

- **Subscription page, Anomaly detection tab** (shown while detection is on). The method, sensitivity, lookback, minimum data points and alert directions; a chart
  of the last 30 days of row counts with the baseline and the lower and upper thresholds; and the recent detection
  events.
- **Control Tower.** An **Anomaly** badge on subscriptions with detection on, a per-day sparkline of anomaly events and
  the anomaly count, and the recent anomalies in the row detail. See [Control Tower](/features/control-tower/).

The chart route is `GET /beacon/api/subscriptions/{id}/anomaly-chart?days=30`. It needs read access to the subscription,
and its run points cover only runs the caller may read.

## Data model

Three entities in the `beacon` schema hold anomaly data (PostgreSQL uses snake_case names):

| Entity | Holds |
|--------|-------|
| `AnomalyConfig` | One per subscription: `Enabled`, `DetectionMethod`, `Sensitivity`, `LookbackDays`, `AlertOnIncrease`, `AlertOnDecrease`, `MinimumDataPoints`. |
| `AnomalyBaseline` | One per stored run: `SubscriptionId`, `ExecutionTime`, `MetricValue` (the row count). |
| `AnomalyEvent` | One per detected anomaly: `SubscriptionId`, `NotificationId`, `DetectedTime`, `CurrentValue`, `BaselineMean`, `BaselineStdDev`, `ZScore`, `Severity`, `Explanation`, `Acknowledged`, `AcknowledgedBy`. |

## Service API

`IAnomalyDetectionService` (in `Beacon.Core.Services`) is what the subscription run uses. A host can call it in-process:

| Method | What it does |
|--------|--------------|
| `EvaluateAnomalyAsync(subscriptionId, rowCount)` | Evaluates a row count against the stored baseline. |
| `StoreBaselineAsync(subscriptionId, metricValue, executionTime)` | Stores a baseline value. |
| `GetHistoricalMetricsAsync(subscriptionId, lookbackDays)` | Baseline values of the window, oldest first. |
| `RecordAnomalyEventAsync(subscriptionId, evaluation, notificationId)` | Records an anomaly event. |
| `GetAnomalyConfigAsync(subscriptionId)` | The configuration as `AnomalyConfigData`, or `null`. |
| `SaveAnomalyConfigAsync(AnomalyConfigData)` | Creates or updates the configuration of a subscription. |
| `GetAnomalyChartDataAsync(subscriptionId, lookbackDays, runScope)` | Chart data, limited to the runs `runScope` may read. |

`AnomalyEvaluationResult` has `IsAnomaly`, `CurrentValue`, `BaselineMean`, `BaselineStdDev`, `ZScore`, `Explanation`,
`Severity` and `HistoricalDataPoints`. `BaselineStdDev` and `ZScore` are set by the standard deviation method only.

```csharp
await anomalyDetectionService.SaveAnomalyConfigAsync(new AnomalyConfigData
{
    SubscriptionId = subscriptionId,
    Enabled = true,
    DetectionMethod = AnomalyDetectionMethod.PercentageChange,
    Sensitivity = AnomalySensitivity.Medium,
    LookbackDays = 30,
    MinimumDataPoints = 14,
    AlertOnIncrease = true,
    AlertOnDecrease = true
}, cancellationToken);
```

`SaveAnomalyConfigAsync` does not check who calls it; a host that exposes it decides who may change a subscription's
configuration.

## Related

- [Subscriptions](/features/subscriptions/): where anomaly detection is configured
- [Control Tower](/features/control-tower/): anomaly counts and sparklines across subscriptions
- [Notifications](/features/notifications/): what recipients receive
- [Tasks](/features/tasks/): work items opened by runs with rows

# API Contracts: AI Monitoring Operations

**Feature**: 005-ai-integration
**Category**: Unsupervised AI monitoring configuration and insights

---

## 1. Enable AI Monitoring

**Operation**: Enable or update AI monitoring configuration for a data source.

### Command: EnableAiMonitoringCommand

```csharp
public record EnableAiMonitoringCommand : IRequest<EnableAiMonitoringResult>
{
    public int DataSourceId { get; init; }
    public MonitoringMode Mode { get; init; }
    public MonitoringScheduleFrequency ScheduleFrequency { get; init; }
    public string? CustomCronExpression { get; init; }
    public bool AllowAdaptiveFrequency { get; init; } = true;
    public int BaselinePeriodDays { get; init; } = 30;
    public VerbosityLevel VerbosityLevel { get; init; } = VerbosityLevel.Standard;
    public MonitoringLimits Limits { get; init; } = new();
}

public record MonitoringLimits
{
    public int MaxQueriesPerDay { get; init; } = 100;
    public int MaxTokensPerDay { get; init; } = 100_000;
    public decimal MaxCostPerMonth { get; init; } = 10.00m;
}
```

### Response: EnableAiMonitoringResult

```csharp
public record EnableAiMonitoringResult
{
    public int MonitoringConfigId { get; init; }
    public int DataSourceId { get; init; }
    public bool IsEnabled { get; init; }
    public MonitoringMode Mode { get; init; }
    public DateTime NextScheduledRunAt { get; init; }
    public string Message { get; init; } = null!;
}
```

### Validation Rules

- `DataSourceId` must exist and be accessible by current user
- User must have "Execute" permission on data source
- `BaselinePeriodDays` must be between 7 and 365
- `MaxQueriesPerDay` must be between 10 and 10,000
- `MaxTokensPerDay` must be between 10,000 and 10,000,000
- `MaxCostPerMonth` must be between 1.00 and 1,000.00
- If `ScheduleFrequency` is Custom, `CustomCronExpression` must be valid

### Error Responses

| Error Code | HTTP Status | Description |
|------------|-------------|-------------|
| `DATA_SOURCE_NOT_FOUND` | 404 | Data source does not exist |
| `DATA_SOURCE_UNAUTHORIZED` | 403 | User does not have access |
| `INVALID_CRON_EXPRESSION` | 400 | Custom cron expression is invalid |
| `INVALID_BASELINE_PERIOD` | 400 | Baseline period out of range |
| `INVALID_LIMITS` | 400 | One or more limits out of range |

### Business Rules

1. Creates `AiMonitoringConfiguration` if not exists, updates if exists
2. If enabling for first time, schedules initial baseline learning run
3. Initial baseline learning uses historical data from `BaselinePeriodDays`
4. First anomaly detection runs after baseline is established

### Example Request

```json
{
  "dataSourceId": 42,
  "mode": "TaskMode",
  "scheduleFrequency": "Daily",
  "allowAdaptiveFrequency": true,
  "baselinePeriodDays": 30,
  "verbosityLevel": "Standard",
  "limits": {
    "maxQueriesPerDay": 100,
    "maxTokensPerDay": 100000,
    "maxCostPerMonth": 10.00
  }
}
```

### Example Response

```json
{
  "monitoringConfigId": 501,
  "dataSourceId": 42,
  "isEnabled": true,
  "mode": "TaskMode",
  "nextScheduledRunAt": "2026-01-04T00:00:00Z",
  "message": "AI monitoring enabled. Baseline learning will begin at next scheduled run."
}
```

---

## 2. Disable AI Monitoring

**Operation**: Disable AI monitoring for a data source.

### Command: DisableAiMonitoringCommand

```csharp
public record DisableAiMonitoringCommand : IRequest<DisableAiMonitoringResult>
{
    public int DataSourceId { get; init; }
    public bool ArchiveInsights { get; init; } = false;
}
```

### Response: DisableAiMonitoringResult

```csharp
public record DisableAiMonitoringResult
{
    public int DataSourceId { get; init; }
    public bool WasEnabled { get; init; }
    public int InsightsArchived { get; init; }
    public string Message { get; init; } = null!;
}
```

### Business Rules

1. Sets `IsEnabled = false` on configuration
2. If `ArchiveInsights = true`, archives all pending insights
3. Preserves baseline data for potential re-enablement
4. Cancels any scheduled monitoring runs

---

## 3. Get Monitoring Configuration

**Operation**: Retrieve current AI monitoring configuration and usage stats.

### Query: GetMonitoringConfigurationQuery

```csharp
public record GetMonitoringConfigurationQuery : IRequest<MonitoringConfigurationResult>
{
    public int DataSourceId { get; init; }
}
```

### Response: MonitoringConfigurationResult

```csharp
public record MonitoringConfigurationResult
{
    public int? MonitoringConfigId { get; init; }
    public int DataSourceId { get; init; }
    public bool IsEnabled { get; init; }
    public bool IsConfigured { get; init; }
    public MonitoringMode? Mode { get; init; }
    public MonitoringScheduleFrequency? ScheduleFrequency { get; init; }
    public string? ScheduleCron { get; init; }
    public bool AllowAdaptiveFrequency { get; init; }
    public int BaselinePeriodDays { get; init; }
    public VerbosityLevel VerbosityLevel { get; init; }
    public MonitoringLimits Limits { get; init; } = null!;
    public MonitoringUsage CurrentUsage { get; init; } = null!;
    public DateTime? LastRunAt { get; init; }
    public DateTime? NextScheduledRunAt { get; init; }
    public bool IsPausedDueToLimits { get; init; }
    public int BaselineMetricsCount { get; init; }
    public int PendingInsightsCount { get; init; }
}

public record MonitoringUsage
{
    public int QueriesUsedToday { get; init; }
    public int TokensUsedToday { get; init; }
    public decimal CostUsedThisMonth { get; init; }
    public decimal UsagePercentage { get; init; }  // Highest of the three
    public bool IsWarningThreshold { get; init; }  // >= 80%
}
```

### Example Response

```json
{
  "monitoringConfigId": 501,
  "dataSourceId": 42,
  "isEnabled": true,
  "isConfigured": true,
  "mode": "TaskMode",
  "scheduleFrequency": "Daily",
  "allowAdaptiveFrequency": true,
  "baselinePeriodDays": 30,
  "verbosityLevel": "Standard",
  "limits": {
    "maxQueriesPerDay": 100,
    "maxTokensPerDay": 100000,
    "maxCostPerMonth": 10.00
  },
  "currentUsage": {
    "queriesUsedToday": 45,
    "tokensUsedToday": 52000,
    "costUsedThisMonth": 4.25,
    "usagePercentage": 52.0,
    "isWarningThreshold": false
  },
  "lastRunAt": "2026-01-03T00:00:00Z",
  "nextScheduledRunAt": "2026-01-04T00:00:00Z",
  "isPausedDueToLimits": false,
  "baselineMetricsCount": 24,
  "pendingInsightsCount": 3
}
```

---

## 4. List AI Insights

**Operation**: Retrieve AI-discovered insights for a data source.

### Query: ListAiInsightsQuery

```csharp
public record ListAiInsightsQuery : IRequest<ListAiInsightsResult>
{
    public int DataSourceId { get; init; }
    public InsightFilter? Filter { get; init; }
    public int PageNumber { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}

public record InsightFilter
{
    public InsightStatus? Status { get; init; }
    public InsightSeverity? MinSeverity { get; init; }
    public AnomalyType? AnomalyType { get; init; }
    public DateTime? DetectedAfter { get; init; }
    public DateTime? DetectedBefore { get; init; }
}
```

### Response: ListAiInsightsResult

```csharp
public record ListAiInsightsResult
{
    public List<AiInsightSummary> Items { get; init; } = new();
    public int TotalCount { get; init; }
    public int PageNumber { get; init; }
    public int PageSize { get; init; }
    public InsightsSummary Summary { get; init; } = null!;
}

public record AiInsightSummary
{
    public int Id { get; init; }
    public AnomalyType AnomalyType { get; init; }
    public InsightSeverity Severity { get; init; }
    public InsightStatus Status { get; init; }
    public string Title { get; init; } = null!;
    public string MetricName { get; init; } = null!;
    public decimal? DeviationPercentage { get; init; }
    public DateTime DetectedAt { get; init; }
    public decimal? AiConfidenceScore { get; init; }
    public bool HasDraftAlert { get; init; }
}

public record InsightsSummary
{
    public int TotalNew { get; init; }
    public int TotalReviewed { get; init; }
    public int TotalDismissed { get; init; }
    public int TotalConverted { get; init; }
    public Dictionary<string, int> BySeverity { get; init; } = new();
    public Dictionary<string, int> ByAnomalyType { get; init; } = new();
}
```

### Example Response

```json
{
  "items": [
    {
      "id": 1001,
      "anomalyType": "Statistical",
      "severity": "High",
      "status": "New",
      "title": "Sales dropped 25% compared to 7-day average",
      "metricName": "orders.daily_revenue",
      "deviationPercentage": -25.3,
      "detectedAt": "2026-01-03T08:00:00Z",
      "aiConfidenceScore": 0.92,
      "hasDraftAlert": false
    },
    {
      "id": 1002,
      "anomalyType": "MissingData",
      "severity": "Critical",
      "status": "New",
      "title": "No new records in audit_logs for 6 hours",
      "metricName": "audit_logs.hourly_count",
      "deviationPercentage": null,
      "detectedAt": "2026-01-03T14:00:00Z",
      "aiConfidenceScore": 0.98,
      "hasDraftAlert": false
    }
  ],
  "totalCount": 5,
  "pageNumber": 1,
  "pageSize": 20,
  "summary": {
    "totalNew": 3,
    "totalReviewed": 1,
    "totalDismissed": 1,
    "totalConverted": 0,
    "bySeverity": { "Low": 1, "Medium": 1, "High": 2, "Critical": 1 },
    "byAnomalyType": { "Statistical": 2, "MissingData": 1, "TrendChange": 1, "VolumeAnomaly": 1 }
  }
}
```

---

## 5. Get AI Insight Detail

**Operation**: Retrieve full details of a specific AI insight.

### Query: GetAiInsightDetailQuery

```csharp
public record GetAiInsightDetailQuery : IRequest<AiInsightDetailResult>
{
    public int InsightId { get; init; }
}
```

### Response: AiInsightDetailResult

```csharp
public record AiInsightDetailResult
{
    public int Id { get; init; }
    public int DataSourceId { get; init; }
    public string DataSourceName { get; init; } = null!;
    public AnomalyType AnomalyType { get; init; }
    public InsightSeverity Severity { get; init; }
    public InsightStatus Status { get; init; }
    public string MetricName { get; init; } = null!;
    public decimal? CurrentValue { get; init; }
    public decimal? ExpectedValue { get; init; }
    public decimal? DeviationPercentage { get; init; }
    public string Title { get; init; } = null!;
    public string Description { get; init; } = null!;
    public string? SuggestedAction { get; init; }
    public string? SuggestedQuery { get; init; }
    public RelatedDataInfo? RelatedData { get; init; }
    public DateTime DetectedAt { get; init; }
    public DateTime? ReviewedAt { get; init; }
    public string? ReviewedByUserName { get; init; }
    public int? DraftAlertId { get; init; }
    public decimal? AiConfidenceScore { get; init; }
}

public record RelatedDataInfo
{
    public List<DataPoint> HistoricalValues { get; init; } = new();
    public List<string> PotentialCauses { get; init; } = new();
    public List<string> RelatedMetrics { get; init; } = new();
}

public record DataPoint
{
    public DateTime Timestamp { get; init; }
    public decimal Value { get; init; }
}
```

---

## 6. Review AI Insight

**Operation**: Mark an insight as reviewed or dismissed.

### Command: ReviewAiInsightCommand

```csharp
public record ReviewAiInsightCommand : IRequest<ReviewAiInsightResult>
{
    public int InsightId { get; init; }
    public InsightReviewAction Action { get; init; }
    public string? Notes { get; init; }
}

public enum InsightReviewAction
{
    MarkReviewed,   // Acknowledge but take no action
    Dismiss,        // Mark as not relevant / false positive
    CreateAlert     // Convert to ongoing alert (separate command)
}
```

### Response: ReviewAiInsightResult

```csharp
public record ReviewAiInsightResult
{
    public int InsightId { get; init; }
    public InsightStatus NewStatus { get; init; }
    public DateTime ReviewedAt { get; init; }
    public string Message { get; init; } = null!;
}
```

### Business Rules

1. Only insights with status `New` can be reviewed
2. `MarkReviewed` changes status to `Reviewed`
3. `Dismiss` changes status to `Dismissed`
4. `CreateAlert` triggers separate `ConvertInsightToAlertCommand`

---

## 7. Convert Insight to Alert

**Operation**: Create a draft alert from an AI insight.

### Command: ConvertInsightToAlertCommand

```csharp
public record ConvertInsightToAlertCommand : IRequest<ConvertInsightToAlertResult>
{
    public int InsightId { get; init; }
    public string? AlertName { get; init; }  // Optional: defaults to insight title
    public bool UseAiSuggestedQuery { get; init; } = true;
    public string? CustomQuery { get; init; }
}
```

### Response: ConvertInsightToAlertResult

```csharp
public record ConvertInsightToAlertResult
{
    public int InsightId { get; init; }
    public int AlertConfigId { get; init; }
    public string AlertName { get; init; } = null!;
    public string GeneratedSql { get; init; } = null!;
    public AlertStatus Status { get; init; }
    public string Message { get; init; } = null!;
}
```

### Business Rules

1. Creates `AiAlertConfiguration` with `IsAiGenerated = true`
2. Links insight via `DraftAlertId`
3. Changes insight status to `ConvertedToAlert`
4. Alert created in `Draft` status for user review
5. If `UseAiSuggestedQuery = false`, `CustomQuery` must be provided

### Example Request

```json
{
  "insightId": 1001,
  "alertName": "Daily Sales Drop Alert",
  "useAiSuggestedQuery": true
}
```

### Example Response

```json
{
  "insightId": 1001,
  "alertConfigId": 601,
  "alertName": "Daily Sales Drop Alert",
  "generatedSql": "SELECT ... FROM orders WHERE ...",
  "status": "Draft",
  "message": "Draft alert created from insight. Review and activate when ready."
}
```

---

## 8. Get Monitoring Baselines

**Operation**: Retrieve learned baseline metrics for a data source.

### Query: GetMonitoringBaselinesQuery

```csharp
public record GetMonitoringBaselinesQuery : IRequest<GetMonitoringBaselinesResult>
{
    public int DataSourceId { get; init; }
    public BaselineType? FilterByType { get; init; }
    public int PageNumber { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}
```

### Response: GetMonitoringBaselinesResult

```csharp
public record GetMonitoringBaselinesResult
{
    public List<BaselineSummary> Items { get; init; } = new();
    public int TotalCount { get; init; }
    public int PageNumber { get; init; }
    public int PageSize { get; init; }
}

public record BaselineSummary
{
    public int Id { get; init; }
    public string MetricName { get; init; } = null!;
    public BaselineType BaselineType { get; init; }
    public decimal? MeanValue { get; init; }
    public decimal? StdDeviation { get; init; }
    public TrendDirection? TrendDirection { get; init; }
    public decimal? DynamicThresholdLow { get; init; }
    public decimal? DynamicThresholdHigh { get; init; }
    public int SampleCount { get; init; }
    public decimal? ConfidenceScore { get; init; }
    public DateTime LastUpdatedAt { get; init; }
}
```

---

## 9. Trigger Manual Analysis

**Operation**: Manually trigger AI monitoring analysis (outside schedule).

### Command: TriggerManualAnalysisCommand

```csharp
public record TriggerManualAnalysisCommand : IRequest<TriggerManualAnalysisResult>
{
    public int DataSourceId { get; init; }
    public bool IgnoreLimits { get; init; } = false;  // Admin only
}
```

### Response: TriggerManualAnalysisResult

```csharp
public record TriggerManualAnalysisResult
{
    public int DataSourceId { get; init; }
    public bool AnalysisStarted { get; init; }
    public int? JobId { get; init; }
    public string Message { get; init; } = null!;
}
```

### Business Rules

1. Checks limits before running (unless `IgnoreLimits = true` and user is admin)
2. If limits exceeded, returns error with usage details
3. Queues analysis job for immediate execution
4. Returns job ID for status tracking

---

## 10. Adjust Alert Parameters (AI Self-Adjustment)

**Operation**: AI adjusts parameters on alerts it created to reduce false positives.

### Command: AdjustAlertParametersCommand

```csharp
public record AdjustAlertParametersCommand : IRequest<AdjustAlertParametersResult>
{
    public int AlertConfigId { get; init; }
    public AlertAdjustment Adjustment { get; init; } = null!;
}

public record AlertAdjustment
{
    public string NewSql { get; init; } = null!;
    public string Reasoning { get; init; } = null!;
    public decimal ExpectedFalsePositiveReduction { get; init; }
}
```

### Response: AdjustAlertParametersResult

```csharp
public record AdjustAlertParametersResult
{
    public int AlertConfigId { get; init; }
    public bool WasAdjusted { get; init; }
    public string PreviousSql { get; init; } = null!;
    public string NewSql { get; init; } = null!;
    public string Reasoning { get; init; } = null!;
    public int AdjustmentVersion { get; init; }
}
```

### Business Rules

1. Only applies to alerts where `IsAiGenerated = true`
2. For user-created alerts, creates suggestion instead (see SuggestAlertAdjustment)
3. Stores previous SQL for potential rollback
4. Tracks adjustment history with version number

---

## 11. Suggest Alert Adjustment (for user-created alerts)

**Operation**: AI suggests adjustments for user-created alerts (does not auto-apply).

### Query: SuggestAlertAdjustmentQuery

```csharp
public record SuggestAlertAdjustmentQuery : IRequest<AlertAdjustmentSuggestion>
{
    public int AlertConfigId { get; init; }
}
```

### Response: AlertAdjustmentSuggestion

```csharp
public record AlertAdjustmentSuggestion
{
    public int AlertConfigId { get; init; }
    public bool HasSuggestion { get; init; }
    public string? CurrentSql { get; init; }
    public string? SuggestedSql { get; init; }
    public string? Reasoning { get; init; }
    public decimal? ExpectedImprovement { get; init; }
    public int RecentFalsePositives { get; init; }
    public int RecentTruePositives { get; init; }
}
```

### Business Rules

1. Analyzes alert execution history
2. Identifies patterns in false positives
3. Suggests threshold or condition adjustments
4. User must manually apply via `RefineAlertQueryCommand`

---

## Summary

**Total Contracts**: 11
- 6 Commands (Enable, Disable, Review, Convert, TriggerManual, AdjustParameters)
- 5 Queries (GetConfig, ListInsights, GetInsightDetail, GetBaselines, SuggestAdjustment)

**Key Features**:
- Per-data-source monitoring configuration
- Task mode (draft alerts) vs Notification mode (direct alerts)
- Configurable limits with soft warning (80%) and hard stop (100%)
- AI insights with severity levels and anomaly types
- Convert insights to ongoing alerts
- AI self-adjustment for AI-created alerts
- Suggestions (not auto-apply) for user-created alerts
- Manual analysis trigger with limit checks

**Integration Points**:
- Links to `AiAlertConfiguration` via `IsAiGenerated` flag
- Uses existing notification channels for Notification mode
- Leverages existing `Subscription` system for activated alerts

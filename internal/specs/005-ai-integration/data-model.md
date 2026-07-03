# Data Model: AI Integration

**Date**: 2026-01-03
**Feature**: 005-ai-integration

---

## Entity Relationship Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│                         Core Entities                                │
└─────────────────────────────────────────────────────────────────────┘

DataSource (existing)
    │
    ├──< DataSourceDocumentation
    │       │
    │       ├──< DocumentationSection
    │       │
    │       ├──< DocumentationVersion (history)
    │       │
    │       ├──< FieldAnalysis (per-column metrics)
    │       │
    │       ├──< DocumentationExport (cached HTML)
    │       │
    │       ├──< DiagramGroup (ERD table groupings)
    │       │
    │       ├──< SchemaSnapshot (schema state at generation)
    │       │
    │       └──< SchemaChange (detected changes history)
    │
    ├──< AiAlertConfiguration ──────────────────┐
    │       │                                   │
    │       └──< AiConversationHistory          │ (IsAiGenerated link)
    │                                           │
    └──< AiMonitoringConfiguration              │
            │                                   │
            ├──< AiMonitoringBaseline           │
            │                                   │
            └──< AiInsight ─────────────────────┘
                    (converts to draft alert)


User (existing)
    │
    └──< AiUsageMetrics


LlmProvider (configuration)
    │
    └── AiPromptTemplate
            │
            └──< PromptTemplateVersion (version history)
```

---

## 1. DataSourceDocumentation

**Purpose**: Stores AI-generated documentation for a data source with version history and editing capabilities.

**Lifecycle**: Created when user requests AI documentation generation, updated when regenerated or manually edited, archived when data source is deleted.

### Fields

| Field | Type | Nullable | Description |
|-------|------|----------|-------------|
| `Id` | int | No | Primary key |
| `DataSourceId` | int | No | Foreign key to DataSource |
| `Title` | string(500) | No | Documentation title (default: "{DataSource.Name} Documentation") |
| `GeneratedByModel` | string(100) | No | LLM model used (e.g., "claude-sonnet-4.5") |
| `GeneratedAt` | DateTime | No | Timestamp of generation |
| `GeneratedByUserId` | int | No | User who requested generation |
| `LastModifiedByUserId` | int | Yes | User who last edited |
| `LastModifiedAt` | DateTime | Yes | Last edit timestamp |
| `Status` | DocumentationStatus | No | Draft, Published, Archived |
| `TablesAnalyzed` | int | No | Number of tables analyzed |
| `TokensUsed` | int | No | Total tokens consumed |
| `EstimatedCost` | decimal(10,4) | No | Estimated LLM cost |
| `Metadata` | string | Yes | JSON: { tableFilter, rowSamples, excludedTables } |
| `CreatedAt` | DateTime | No | Record creation timestamp |
| `ModifiedAt` | DateTime | No | Record modification timestamp |
| `CreatedBy` | string(100) | No | User who created |
| `ModifiedBy` | string(100) | No | User who last modified |
| `IsArchived` | bool | No | Soft delete flag |

### Relationships

- **DataSource** (1:N): One data source can have multiple documentation versions
- **DocumentationSection** (1:N): One documentation has multiple sections
- **DocumentationVersion** (1:N): Version history for auditing

### Validation Rules

- `Title` must be non-empty and max 500 characters
- `DataSourceId` must reference existing, non-archived DataSource
- `GeneratedByModel` must be valid LLM model identifier
- `TablesAnalyzed` must be > 0
- `TokensUsed` must be > 0
- `EstimatedCost` must be >= 0
- When `Status` = Published, at least one DocumentationSection must exist

### State Transitions

```
[Draft] ──(Publish)──> [Published]
   │                        │
   └────(Archive)───────────┘──> [Archived]
          │
          └────(Restore)───> [Draft]
```

### Indexes

```sql
CREATE INDEX IX_DataSourceDocumentation_DataSourceId ON DataSourceDocumentation(DataSourceId);
CREATE INDEX IX_DataSourceDocumentation_Status ON DataSourceDocumentation(Status);
CREATE INDEX IX_DataSourceDocumentation_GeneratedAt ON DataSourceDocumentation(GeneratedAt DESC);
```

---

## 2. DocumentationSection

**Purpose**: Individual sections of documentation (table descriptions, column descriptions, relationships) with indicators for AI-generated vs user-edited content.

**Lifecycle**: Created during AI generation, updated when user edits or AI regenerates, deleted when parent documentation is archived.

### Fields

| Field | Type | Nullable | Description |
|-------|------|----------|-------------|
| `Id` | int | No | Primary key |
| `DocumentationId` | int | No | Foreign key to DataSourceDocumentation |
| `SectionType` | SectionType | No | Overview, TableDescription, ColumnDescription, Relationships, DataQuality |
| `TableName` | string(200) | Yes | Table name (if applicable) |
| `ColumnName` | string(200) | Yes | Column name (if applicable) |
| `SortOrder` | int | No | Display order |
| `AiGeneratedContent` | string | No | Original AI-generated text (immutable) |
| `UserEditedContent` | string | Yes | User-edited text (nullable if not edited) |
| `IsUserEdited` | bool | No | Flag indicating user has edited |
| `ContentFormat` | ContentFormat | No | Markdown, PlainText, Html |
| `ConfidenceScore` | decimal(3,2) | Yes | AI confidence (0.00-1.00) |
| `CreatedAt` | DateTime | No | Record creation timestamp |
| `ModifiedAt` | DateTime | No | Record modification timestamp |
| `CreatedBy` | string(100) | No | User who created |
| `ModifiedBy` | string(100) | No | User who last modified |

### Relationships

- **DataSourceDocumentation** (N:1): Many sections belong to one documentation

### Validation Rules

- `SectionType` must be valid enum value
- `SortOrder` must be >= 0
- `AiGeneratedContent` must be non-empty
- `ContentFormat` must be valid enum value
- `ConfidenceScore` must be between 0.00 and 1.00 if provided
- If `IsUserEdited` = true, `UserEditedContent` must not be null
- If `SectionType` = TableDescription or ColumnDescription, corresponding name fields must be provided

### Display Logic

```csharp
public string GetDisplayContent()
{
    return IsUserEdited ? UserEditedContent! : AiGeneratedContent;
}

public bool HasBeenModified()
{
    return IsUserEdited && UserEditedContent != AiGeneratedContent;
}
```

### Indexes

```sql
CREATE INDEX IX_DocumentationSection_DocumentationId ON DocumentationSection(DocumentationId);
CREATE INDEX IX_DocumentationSection_TableName ON DocumentationSection(TableName);
CREATE INDEX IX_DocumentationSection_SectionType ON DocumentationSection(SectionType);
```

---

## 3. DocumentationVersion

**Purpose**: Version history for documentation to support rollback and audit trail.

**Lifecycle**: Created on every documentation publish or significant edit.

### Fields

| Field | Type | Nullable | Description |
|-------|------|----------|-------------|
| `Id` | int | No | Primary key |
| `DocumentationId` | int | No | Foreign key to DataSourceDocumentation |
| `VersionNumber` | int | No | Sequential version number |
| `CreatedAt` | DateTime | No | Version creation timestamp |
| `CreatedByUserId` | int | No | User who created this version |
| `ChangeDescription` | string(1000) | Yes | Summary of changes |
| `SnapshotJson` | string | No | JSON snapshot of entire documentation |
| `SectionsCount` | int | No | Number of sections in this version |
| `TokensUsed` | int | Yes | Tokens used if AI regenerated |

### Relationships

- **DataSourceDocumentation** (N:1): Many versions belong to one documentation

### Validation Rules

- `VersionNumber` must be > 0 and sequential
- `SnapshotJson` must be valid JSON
- `SectionsCount` must match actual sections in snapshot

### Indexes

```sql
CREATE INDEX IX_DocumentationVersion_DocumentationId ON DocumentationVersion(DocumentationId);
CREATE INDEX IX_DocumentationVersion_CreatedAt ON DocumentationVersion(CreatedAt DESC);
```

---

## 4. AiAlertConfiguration

**Purpose**: Stores natural language alert descriptions, AI-generated SQL queries, conversation history, and user feedback for AI-powered alerts.

**Lifecycle**: Created when user describes alert in natural language, updated during refinement, activated when query is finalized, archived when alert is deleted.

### Fields

| Field | Type | Nullable | Description |
|-------|------|----------|-------------|
| `Id` | int | No | Primary key |
| `DataSourceId` | int | No | Foreign key to DataSource |
| `Name` | string(200) | No | Alert name |
| `NaturalLanguageDescription` | string(2000) | No | Original user description |
| `GeneratedSql` | string | No | AI-generated SQL query |
| `FinalSql` | string | Yes | User-modified SQL (if edited) |
| `GeneratedByModel` | string(100) | No | LLM model used |
| `GenerationReasoning` | string | Yes | AI explanation of query logic |
| `ConfidenceScore` | decimal(3,2) | Yes | AI confidence in query (0.00-1.00) |
| `Status` | AlertStatus | No | Draft, Active, Paused, Archived |
| `ValidationErrors` | string | Yes | JSON array of validation errors |
| `UserFeedback` | string(2000) | Yes | User feedback on AI-generated query |
| `SubscriptionId` | int | Yes | Foreign key to Subscription (if activated) |
| `ConversationTurns` | int | No | Number of refinement iterations |
| `TokensUsed` | int | No | Total tokens consumed |
| `EstimatedCost` | decimal(10,4) | No | Estimated LLM cost |
| `CreatedAt` | DateTime | No | Record creation timestamp |
| `ModifiedAt` | DateTime | No | Record modification timestamp |
| `CreatedBy` | string(100) | No | User who created |
| `ModifiedBy` | string(100) | No | User who last modified |
| `IsArchived` | bool | No | Soft delete flag |

### Relationships

- **DataSource** (N:1): Many alert configurations belong to one data source
- **Subscription** (1:1 optional): One alert config may link to one subscription
- **AiConversationHistory** (1:N): One alert config has multiple conversation turns

### Validation Rules

- `NaturalLanguageDescription` must be non-empty, min 10 characters
- `GeneratedSql` must be non-empty
- `GeneratedByModel` must be valid LLM model identifier
- `ConfidenceScore` must be between 0.00 and 1.00 if provided
- `ConversationTurns` must be >= 0
- `TokensUsed` must be > 0
- `EstimatedCost` must be >= 0
- When `Status` = Active, `SubscriptionId` must be provided
- When `Status` = Active, `ValidationErrors` must be null/empty

### State Transitions

```
[Draft] ──(Validate & Activate)──> [Active]
   │                                   │
   │                                   ├──(Pause)──> [Paused]
   │                                   │                │
   │                                   │                └──(Resume)──> [Active]
   │                                   │
   └──────────(Archive)────────────────┘──> [Archived]
```

### Indexes

```sql
CREATE INDEX IX_AiAlertConfiguration_DataSourceId ON AiAlertConfiguration(DataSourceId);
CREATE INDEX IX_AiAlertConfiguration_Status ON AiAlertConfiguration(Status);
CREATE INDEX IX_AiAlertConfiguration_SubscriptionId ON AiAlertConfiguration(SubscriptionId);
```

---

## 5. AiConversationHistory

**Purpose**: Tracks back-and-forth conversation between user and AI during alert refinement for context-aware query generation.

**Lifecycle**: Created for each message in conversation, persisted for learning and debugging.

### Fields

| Field | Type | Nullable | Description |
|-------|------|----------|-------------|
| `Id` | int | No | Primary key |
| `AiAlertConfigurationId` | int | No | Foreign key to AiAlertConfiguration |
| `TurnNumber` | int | No | Sequential conversation turn (1, 2, 3...) |
| `Role` | ConversationRole | No | User, Assistant, System |
| `MessageContent` | string | No | Message text |
| `TokensUsed` | int | No | Tokens consumed for this message |
| `Model` | string(100) | No | LLM model used |
| `Timestamp` | DateTime | No | Message timestamp |
| `Metadata` | string | Yes | JSON: { temperature, topP, stopSequences } |

### Relationships

- **AiAlertConfiguration** (N:1): Many conversation turns belong to one alert config

### Validation Rules

- `TurnNumber` must be > 0 and sequential
- `Role` must be valid enum value
- `MessageContent` must be non-empty
- `TokensUsed` must be > 0
- `Model` must be valid LLM model identifier

### Conversation Pattern

```
Turn 1: User: "Alert me when sales drop more than 20% compared to last week"
Turn 2: Assistant: "I've generated this SQL query: ..."
Turn 3: User: "Actually, compare to same day last week, not the whole week"
Turn 4: Assistant: "Updated query: ..."
```

### Indexes

```sql
CREATE INDEX IX_AiConversationHistory_AlertConfigId ON AiConversationHistory(AiAlertConfigurationId);
CREATE INDEX IX_AiConversationHistory_TurnNumber ON AiConversationHistory(TurnNumber);
CREATE INDEX IX_AiConversationHistory_Timestamp ON AiConversationHistory(Timestamp DESC);
```

---

## 6. AiUsageMetrics

**Purpose**: Tracks AI API calls, token usage, costs, and performance metrics for monitoring, billing, and optimization.

**Lifecycle**: Created for every AI API call, archived after retention period (e.g., 90 days for detailed metrics, aggregated for long-term trends).

### Fields

| Field | Type | Nullable | Description |
|-------|------|----------|-------------|
| `Id` | int | No | Primary key |
| `UserId` | int | Yes | User who triggered the request |
| `DataSourceId` | int | Yes | Related data source (if applicable) |
| `QueryId` | int | Yes | Related query (if applicable) |
| `DocumentationId` | int | Yes | Related documentation (if applicable) |
| `AlertConfigId` | int | Yes | Related alert configuration (if applicable) |
| `Provider` | string(50) | No | OpenAI, Anthropic, AzureOpenAI |
| `Model` | string(100) | No | Specific model (e.g., "claude-sonnet-4.5") |
| `OperationType` | OperationType | No | SchemaAnalysis, QueryGeneration, QueryRefinement, Documentation, Validation |
| `InputTokens` | int | No | Tokens in request |
| `OutputTokens` | int | No | Tokens in response |
| `TotalTokens` | int | No | InputTokens + OutputTokens |
| `EstimatedCost` | decimal(10,4) | No | Calculated cost based on provider pricing |
| `PromptCacheHit` | bool | No | Whether prompt cache was used (Claude) |
| `ResponseTimeMs` | int | No | API response time in milliseconds |
| `Success` | bool | No | Whether request succeeded |
| `ErrorMessage` | string(500) | Yes | Error message if failed |
| `Timestamp` | DateTime | No | Request timestamp |
| `CreatedAt` | DateTime | No | Record creation timestamp |
| `IsArchived` | bool | No | Soft delete flag |

### Relationships

- **User** (N:1 optional): Many usage records may belong to one user
- **DataSource** (N:1 optional): May relate to a data source
- **Query** (N:1 optional): May relate to a query
- **DataSourceDocumentation** (N:1 optional): May relate to documentation
- **AiAlertConfiguration** (N:1 optional): May relate to an alert configuration

### Validation Rules

- `Provider` must be valid enum value
- `Model` must be valid model identifier for the provider
- `OperationType` must be valid enum value
- `InputTokens` must be >= 0
- `OutputTokens` must be >= 0
- `TotalTokens` must equal `InputTokens` + `OutputTokens`
- `EstimatedCost` must be >= 0
- `ResponseTimeMs` must be > 0 if `Success` = true
- If `Success` = false, `ErrorMessage` must be provided

### Aggregation Queries

```sql
-- Monthly cost by user
SELECT
    UserId,
    DATE_TRUNC('month', Timestamp) AS Month,
    SUM(EstimatedCost) AS TotalCost,
    SUM(TotalTokens) AS TotalTokens
FROM AiUsageMetrics
WHERE IsArchived = false
GROUP BY UserId, Month;

-- Cost by operation type
SELECT
    OperationType,
    COUNT(*) AS RequestCount,
    SUM(EstimatedCost) AS TotalCost,
    AVG(ResponseTimeMs) AS AvgResponseTime
FROM AiUsageMetrics
WHERE Timestamp >= NOW() - INTERVAL '30 days'
GROUP BY OperationType;
```

### Indexes

```sql
CREATE INDEX IX_AiUsageMetrics_UserId ON AiUsageMetrics(UserId);
CREATE INDEX IX_AiUsageMetrics_Timestamp ON AiUsageMetrics(Timestamp DESC);
CREATE INDEX IX_AiUsageMetrics_Provider ON AiUsageMetrics(Provider);
CREATE INDEX IX_AiUsageMetrics_OperationType ON AiUsageMetrics(OperationType);
```

---

## 7. AiPromptTemplate

**Purpose**: Configurable prompt templates for different AI tasks with versioning and A/B testing support.

**Lifecycle**: Created by administrators, versioned on changes, activated for specific operations.

### Fields

| Field | Type | Nullable | Description |
|-------|------|----------|-------------|
| `Id` | int | No | Primary key |
| `Name` | string(200) | No | Template name |
| `OperationType` | OperationType | No | SchemaAnalysis, QueryGeneration, etc. |
| `PromptTemplate` | string | No | Template with {{variables}} |
| `SystemPrompt` | string | Yes | Optional system message |
| `Version` | int | No | Template version number |
| `IsActive` | bool | No | Whether this template is in use |
| `Temperature` | decimal(2,1) | No | LLM temperature (0.0-2.0) |
| `MaxTokens` | int | No | Maximum output tokens |
| `VariableDefinitions` | string | No | JSON schema of variables |
| `Description` | string(1000) | Yes | Template description/notes |
| `CreatedAt` | DateTime | No | Record creation timestamp |
| `ModifiedAt` | DateTime | No | Record modification timestamp |
| `CreatedBy` | string(100) | No | User who created |
| `ModifiedBy` | string(100) | No | User who last modified |

### Example Template

```json
{
  "Name": "Schema Analysis Template",
  "OperationType": "SchemaAnalysis",
  "PromptTemplate": "Analyze the following database schema and provide descriptions for each table and column.\n\nTables: {{tableCount}}\nSchema:\n{{schemaJson}}\n\nSample Data (first {{sampleRows}} rows per table):\n{{sampleDataJson}}",
  "SystemPrompt": "You are an expert database analyst. Provide clear, concise descriptions that help developers understand the data model.",
  "Temperature": 0.3,
  "MaxTokens": 4096,
  "VariableDefinitions": {
    "tableCount": "integer",
    "schemaJson": "string",
    "sampleRows": "integer",
    "sampleDataJson": "string"
  }
}
```

### Validation Rules

- `Name` must be unique per OperationType
- `PromptTemplate` must be non-empty
- `Temperature` must be between 0.0 and 2.0
- `MaxTokens` must be > 0
- Only one template per OperationType can have `IsActive` = true
- `VariableDefinitions` must be valid JSON

### Indexes

```sql
CREATE INDEX IX_AiPromptTemplate_OperationType ON AiPromptTemplate(OperationType);
CREATE INDEX IX_AiPromptTemplate_IsActive ON AiPromptTemplate(IsActive);
```

---

## 8. FieldAnalysis

**Purpose**: Stores detailed per-column analysis results from AI field quality analysis, including null percentages, distinct values, detected patterns, and migration recommendations.

**Lifecycle**: Created during field analysis phase of documentation generation, updated if analysis is re-run, linked to documentation for reporting.

### Fields

| Field | Type | Nullable | Description |
|-------|------|----------|-------------|
| `Id` | int | No | Primary key |
| `DocumentationId` | int | No | Foreign key to DataSourceDocumentation |
| `TableName` | string(200) | No | Name of the analyzed table |
| `ColumnName` | string(200) | No | Name of the analyzed column |
| `DataType` | string(100) | No | Column's declared data type |
| `TotalRows` | long | No | Total rows in table at analysis time |
| `SampledRows` | long | No | Number of rows actually analyzed |
| `UsedFullScan` | bool | No | Whether full table scan was performed |
| `NonNullCount` | long | No | Count of non-null values |
| `NullPercentage` | decimal(5,2) | No | Percentage of null values (0.00-100.00) |
| `DistinctValues` | long | No | Count of distinct non-null values |
| `UsageStatus` | FieldUsageStatus | No | Unused, PotentiallyUnused, Used |
| `DetectedPattern` | DetectedDataPattern | Yes | Email, Phone, Date, Json, Url, Numeric, Uuid, None |
| `PatternMatchPercentage` | decimal(5,2) | Yes | % of values matching detected pattern |
| `SuggestedDataType` | string(100) | Yes | Recommended data type (if pattern detected) |
| `MigrationFeasibility` | decimal(5,2) | Yes | % of values that would convert successfully |
| `MigrationIssueCount` | int | Yes | Count of values that would fail conversion |
| `AiRecommendation` | string(2000) | Yes | AI-generated recommendation text |
| `AiConfidenceScore` | decimal(3,2) | Yes | AI confidence in recommendation (0.00-1.00) |
| `SampleValues` | string | Yes | JSON array of sample values for context |
| `AnalyzedAt` | DateTime | No | Timestamp of analysis |
| `CreatedAt` | DateTime | No | Record creation timestamp |
| `ModifiedAt` | DateTime | No | Record modification timestamp |
| `CreatedBy` | string(100) | No | User who created |
| `ModifiedBy` | string(100) | No | User who last modified |

### Relationships

- **DataSourceDocumentation** (N:1): Many field analyses belong to one documentation

### Validation Rules

- `TableName` and `ColumnName` must be non-empty
- `TotalRows` must be > 0
- `SampledRows` must be > 0 and <= `TotalRows`
- `NullPercentage` must be between 0.00 and 100.00
- `NonNullCount` must be <= `TotalRows`
- `DistinctValues` must be <= `NonNullCount`
- If `DetectedPattern` is not None, `PatternMatchPercentage` must be provided
- If `SuggestedDataType` is provided, `MigrationFeasibility` should be provided
- `MigrationFeasibility` must be between 0.00 and 100.00 if provided
- `AiConfidenceScore` must be between 0.00 and 1.00 if provided

### Usage Status Logic

```csharp
public FieldUsageStatus CalculateUsageStatus()
{
    var nonNullPercentage = 100 - NullPercentage;

    return nonNullPercentage switch
    {
        0 => FieldUsageStatus.Unused,           // 0% non-null
        < 1 => FieldUsageStatus.PotentiallyUnused, // <1% non-null
        _ => FieldUsageStatus.Used              // >=1% non-null
    };
}
```

### Sampling Configuration

```csharp
public record SamplingConfig
{
    public const int SamplingThreshold = 10_000;      // Tables > this use sampling
    public const decimal SamplePercentage = 0.10m;    // 10% of rows
    public const int MinSampleSize = 1_000;           // Minimum sample
    public const int MaxSampleSize = 100_000;         // Maximum sample

    public static long CalculateSampleSize(long totalRows)
    {
        if (totalRows <= SamplingThreshold)
            return totalRows; // Full scan

        var sample = (long)(totalRows * SamplePercentage);
        return Math.Clamp(sample, MinSampleSize, MaxSampleSize);
    }
}
```

### Indexes

```sql
CREATE INDEX IX_FieldAnalysis_DocumentationId ON FieldAnalysis(DocumentationId);
CREATE INDEX IX_FieldAnalysis_TableName ON FieldAnalysis(TableName);
CREATE INDEX IX_FieldAnalysis_UsageStatus ON FieldAnalysis(UsageStatus);
CREATE INDEX IX_FieldAnalysis_DetectedPattern ON FieldAnalysis(DetectedPattern);
CREATE INDEX IX_FieldAnalysis_AnalyzedAt ON FieldAnalysis(AnalyzedAt DESC);
```

---

## 9. AiMonitoringConfiguration

**Purpose**: Per-data-source settings for unsupervised AI monitoring including mode, schedule, limits, baseline period, and verbosity level.

**Lifecycle**: Created when user enables AI monitoring on a data source, updated when settings change, soft-deleted when monitoring is disabled.

### Fields

| Field | Type | Nullable | Description |
|-------|------|----------|-------------|
| `Id` | int | No | Primary key |
| `DataSourceId` | int | No | Foreign key to DataSource |
| `IsEnabled` | bool | No | Whether AI monitoring is active |
| `MonitoringMode` | MonitoringMode | No | TaskMode (draft alerts) or NotificationMode (direct) |
| `ScheduleFrequency` | MonitoringScheduleFrequency | No | Hourly, Daily, Weekly |
| `ScheduleCron` | string(100) | Yes | Custom cron expression (optional) |
| `AllowAdaptiveFrequency` | bool | No | Whether AI can increase frequency based on volatility |
| `BaselinePeriodDays` | int | No | Historical days to use for baseline (default 30) |
| `VerbosityLevel` | VerbosityLevel | No | Minimal, Standard, Detailed, Full |
| `MaxQueriesPerDay` | int | No | Limit: queries per day (default 100) |
| `MaxTokensPerDay` | int | No | Limit: tokens per day (default 100,000) |
| `MaxCostPerMonth` | decimal(10,2) | No | Limit: cost per month (default $10) |
| `CurrentDayQueries` | int | No | Current usage: queries today |
| `CurrentDayTokens` | int | No | Current usage: tokens today |
| `CurrentMonthCost` | decimal(10,2) | No | Current usage: cost this month |
| `LastResetDate` | DateTime | No | When daily counters were last reset |
| `LastMonthlyResetDate` | DateTime | No | When monthly counter was last reset |
| `LastRunAt` | DateTime | Yes | When monitoring last executed |
| `NextScheduledRunAt` | DateTime | Yes | Next scheduled execution time |
| `IsPausedDueToLimits` | bool | No | Whether paused due to hitting limits |
| `CreatedAt` | DateTime | No | Record creation timestamp |
| `ModifiedAt` | DateTime | No | Record modification timestamp |
| `CreatedBy` | string(100) | No | User who created |
| `ModifiedBy` | string(100) | No | User who last modified |
| `IsArchived` | bool | No | Soft delete flag |

### Relationships

- **DataSource** (1:1): One configuration per data source
- **AiMonitoringBaseline** (1:N): One configuration has multiple baseline records
- **AiInsight** (1:N): One configuration produces multiple insights

### Validation Rules

- `DataSourceId` must reference existing, non-archived DataSource
- `BaselinePeriodDays` must be between 7 and 365
- `MaxQueriesPerDay` must be between 10 and 10,000
- `MaxTokensPerDay` must be between 10,000 and 10,000,000
- `MaxCostPerMonth` must be between 1.00 and 1,000.00
- If `ScheduleFrequency` is Custom, `ScheduleCron` must be valid cron expression

### Limit Check Logic

```csharp
public bool IsWithinLimits()
{
    return CurrentDayQueries < MaxQueriesPerDay &&
           CurrentDayTokens < MaxTokensPerDay &&
           CurrentMonthCost < MaxCostPerMonth;
}

public decimal GetUsagePercentage()
{
    var queryPct = (decimal)CurrentDayQueries / MaxQueriesPerDay * 100;
    var tokenPct = (decimal)CurrentDayTokens / MaxTokensPerDay * 100;
    var costPct = CurrentMonthCost / MaxCostPerMonth * 100;
    return Math.Max(Math.Max(queryPct, tokenPct), costPct);
}

public bool ShouldSendWarning() => GetUsagePercentage() >= 80;
```

### Indexes

```sql
CREATE UNIQUE INDEX IX_AiMonitoringConfiguration_DataSourceId ON AiMonitoringConfiguration(DataSourceId);
CREATE INDEX IX_AiMonitoringConfiguration_IsEnabled ON AiMonitoringConfiguration(IsEnabled);
CREATE INDEX IX_AiMonitoringConfiguration_NextScheduledRunAt ON AiMonitoringConfiguration(NextScheduledRunAt);
```

---

## 10. AiMonitoringBaseline

**Purpose**: Stores learned "normal" patterns for a data source including statistical baselines for metrics, trends, and dynamic thresholds.

**Lifecycle**: Created during initial baseline learning, continuously updated as AI observes new data, archived when monitoring is disabled.

### Fields

| Field | Type | Nullable | Description |
|-------|------|----------|-------------|
| `Id` | int | No | Primary key |
| `MonitoringConfigId` | int | No | Foreign key to AiMonitoringConfiguration |
| `MetricName` | string(500) | No | Identifier for the metric (e.g., "orders.daily_count") |
| `MetricQuery` | string | No | SQL query that produces this metric |
| `BaselineType` | BaselineType | No | Statistical, Trend, Threshold, Volume |
| `MeanValue` | decimal(18,4) | Yes | Statistical mean |
| `StdDeviation` | decimal(18,4) | Yes | Standard deviation |
| `MinValue` | decimal(18,4) | Yes | Observed minimum |
| `MaxValue` | decimal(18,4) | Yes | Observed maximum |
| `TrendDirection` | TrendDirection | Yes | Increasing, Decreasing, Stable, Volatile |
| `TrendSlope` | decimal(18,6) | Yes | Rate of change per time unit |
| `SeasonalPattern` | string | Yes | JSON: detected seasonal patterns (hourly, daily, weekly) |
| `DynamicThresholdLow` | decimal(18,4) | Yes | AI-calculated low threshold |
| `DynamicThresholdHigh` | decimal(18,4) | Yes | AI-calculated high threshold |
| `SampleCount` | int | No | Number of data points used for baseline |
| `LastUpdatedAt` | DateTime | No | When baseline was last recalculated |
| `ConfidenceScore` | decimal(3,2) | Yes | Confidence in baseline accuracy (0.00-1.00) |
| `CreatedAt` | DateTime | No | Record creation timestamp |
| `ModifiedAt` | DateTime | No | Record modification timestamp |

### Relationships

- **AiMonitoringConfiguration** (N:1): Many baselines belong to one configuration

### Validation Rules

- `MetricName` must be non-empty and max 500 characters
- `MetricQuery` must be valid SELECT statement
- `SampleCount` must be > 0
- `ConfidenceScore` must be between 0.00 and 1.00 if provided
- If `BaselineType` is Statistical, `MeanValue` and `StdDeviation` must be provided
- If `BaselineType` is Threshold, at least one threshold value must be provided

### Anomaly Detection Logic

```csharp
public bool IsAnomaly(decimal currentValue)
{
    if (BaselineType == BaselineType.Statistical && MeanValue.HasValue && StdDeviation.HasValue)
    {
        var zScore = Math.Abs((currentValue - MeanValue.Value) / StdDeviation.Value);
        return zScore > 2.5; // Outside 2.5 standard deviations
    }

    if (BaselineType == BaselineType.Threshold)
    {
        return (DynamicThresholdLow.HasValue && currentValue < DynamicThresholdLow.Value) ||
               (DynamicThresholdHigh.HasValue && currentValue > DynamicThresholdHigh.Value);
    }

    return false;
}
```

### Indexes

```sql
CREATE INDEX IX_AiMonitoringBaseline_MonitoringConfigId ON AiMonitoringBaseline(MonitoringConfigId);
CREATE INDEX IX_AiMonitoringBaseline_MetricName ON AiMonitoringBaseline(MetricName);
CREATE INDEX IX_AiMonitoringBaseline_BaselineType ON AiMonitoringBaseline(BaselineType);
```

---

## 11. AiInsight

**Purpose**: Individual AI-discovered findings from unsupervised monitoring with anomaly details, severity, context, and suggested actions.

**Lifecycle**: Created when AI detects anomaly, updated if status changes, linked to draft alert if user approves.

### Fields

| Field | Type | Nullable | Description |
|-------|------|----------|-------------|
| `Id` | int | No | Primary key |
| `MonitoringConfigId` | int | No | Foreign key to AiMonitoringConfiguration |
| `AnomalyType` | AnomalyType | No | Statistical, TrendChange, MissingData, ThresholdBreach, VolumeAnomaly, CorrelationBreak |
| `Severity` | InsightSeverity | No | Low, Medium, High, Critical |
| `Status` | InsightStatus | No | New, Reviewed, Dismissed, ConvertedToAlert |
| `MetricName` | string(500) | No | Affected metric identifier |
| `CurrentValue` | decimal(18,4) | Yes | Observed value that triggered insight |
| `ExpectedValue` | decimal(18,4) | Yes | Expected/baseline value |
| `DeviationPercentage` | decimal(10,2) | Yes | % deviation from expected |
| `Title` | string(500) | No | Short description (e.g., "Sales dropped 25% yesterday") |
| `Description` | string(4000) | No | Full context based on verbosity setting |
| `SuggestedAction` | string(2000) | Yes | AI recommendation |
| `SuggestedQuery` | string | Yes | SQL query for ongoing monitoring |
| `RelatedData` | string | Yes | JSON: supporting data points, trends, comparisons |
| `DetectedAt` | DateTime | No | When anomaly was detected |
| `ReviewedAt` | DateTime | Yes | When user reviewed |
| `ReviewedByUserId` | int | Yes | User who reviewed |
| `DraftAlertId` | int | Yes | Foreign key to AiAlertConfiguration (if converted) |
| `AiConfidenceScore` | decimal(3,2) | Yes | AI confidence in this insight (0.00-1.00) |
| `CreatedAt` | DateTime | No | Record creation timestamp |
| `ModifiedAt` | DateTime | No | Record modification timestamp |

### Relationships

- **AiMonitoringConfiguration** (N:1): Many insights belong to one configuration
- **AiAlertConfiguration** (1:1 optional): May link to a draft alert if user approves

### Validation Rules

- `Title` must be non-empty and max 500 characters
- `Description` must be non-empty
- If `Status` is ConvertedToAlert, `DraftAlertId` must be provided
- If `Status` is Reviewed or Dismissed, `ReviewedAt` and `ReviewedByUserId` must be provided
- `AiConfidenceScore` must be between 0.00 and 1.00 if provided

### State Transitions

```
[New] ──(User reviews)──> [Reviewed]
  │                            │
  │                            └──(Create alert)──> [ConvertedToAlert]
  │
  └──(User dismisses)──> [Dismissed]
```

### Indexes

```sql
CREATE INDEX IX_AiInsight_MonitoringConfigId ON AiInsight(MonitoringConfigId);
CREATE INDEX IX_AiInsight_Status ON AiInsight(Status);
CREATE INDEX IX_AiInsight_Severity ON AiInsight(Severity);
CREATE INDEX IX_AiInsight_AnomalyType ON AiInsight(AnomalyType);
CREATE INDEX IX_AiInsight_DetectedAt ON AiInsight(DetectedAt DESC);
CREATE INDEX IX_AiInsight_DraftAlertId ON AiInsight(DraftAlertId);
```

---

## 12. DocumentationExport

**Purpose**: Stores cached HTML exports with version tracking to avoid regenerating unchanged documentation.

**Lifecycle**: Created on first HTML export, updated when documentation changes trigger regeneration, archived when documentation is archived.

### Fields

| Field | Type | Nullable | Description |
|-------|------|----------|-------------|
| `Id` | int | No | Primary key |
| `DocumentationId` | int | No | Foreign key to DataSourceDocumentation |
| `ExportFormat` | ExportFormat | No | Html (currently only Html supported) |
| `GeneratedContent` | string | No | Full HTML content (compressed) |
| `ContentHash` | string(64) | No | SHA-256 hash of source documentation for cache invalidation |
| `MermaidDiagramsJson` | string | Yes | JSON: array of Mermaid ERD code per diagram group |
| `GeneratedAt` | DateTime | No | When export was generated |
| `GeneratedByUserId` | int | No | User who triggered generation |
| `FileSizeBytes` | long | No | Size of generated content |
| `GenerationTimeMs` | int | No | Time taken to generate in milliseconds |
| `DocumentationVersionNumber` | int | No | Version of documentation when exported |
| `CreatedAt` | DateTime | No | Record creation timestamp |
| `ModifiedAt` | DateTime | No | Record modification timestamp |
| `CreatedBy` | string(100) | No | User who created |
| `ModifiedBy` | string(100) | No | User who last modified |

### Relationships

- **DataSourceDocumentation** (N:1): Many exports belong to one documentation (though typically only latest is kept)

### Validation Rules

- `DocumentationId` must reference existing, non-archived documentation
- `ContentHash` must be valid SHA-256 hex string (64 characters)
- `FileSizeBytes` must be > 0
- `GenerationTimeMs` must be >= 0

### Cache Invalidation Logic

```csharp
public bool IsCacheValid(string currentDocumentationHash)
{
    return ContentHash == currentDocumentationHash;
}

public static string ComputeDocumentationHash(DataSourceDocumentation doc)
{
    // Combines documentation version, section hashes, and diagram group hashes
    var content = $"{doc.Id}:{doc.ModifiedAt:O}:{doc.Sections.Count}";
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
```

### Indexes

```sql
CREATE UNIQUE INDEX IX_DocumentationExport_DocumentationId ON DocumentationExport(DocumentationId);
CREATE INDEX IX_DocumentationExport_GeneratedAt ON DocumentationExport(GeneratedAt DESC);
```

---

## 13. DiagramGroup

**Purpose**: Stores user-customizable groupings of tables for ERD diagrams, with tracking of AI-suggested vs user-modified state.

**Lifecycle**: Created when AI suggests groups or user creates custom group, updated when user modifies, deleted when documentation is archived.

### Fields

| Field | Type | Nullable | Description |
|-------|------|----------|-------------|
| `Id` | int | No | Primary key |
| `DocumentationId` | int | No | Foreign key to DataSourceDocumentation |
| `Name` | string(200) | No | Group name (e.g., "Order Management", "User & Auth") |
| `Description` | string(1000) | Yes | AI-generated or user-provided description |
| `SortOrder` | int | No | Display order for groups |
| `IsAiGenerated` | bool | No | True if created by AI, false if user-created |
| `IsUserModified` | bool | No | True if user has modified AI-generated group |
| `TableNames` | string | No | JSON array of table names in this group |
| `GroupingCriteria` | DiagramGroupingCriteria | No | How tables were grouped (ForeignKey, Naming, Semantic, Manual) |
| `AiConfidenceScore` | decimal(3,2) | Yes | AI confidence in grouping relevance (0.00-1.00) |
| `CreatedAt` | DateTime | No | Record creation timestamp |
| `ModifiedAt` | DateTime | No | Record modification timestamp |
| `CreatedBy` | string(100) | No | User who created |
| `ModifiedBy` | string(100) | No | User who last modified |

### Relationships

- **DataSourceDocumentation** (N:1): Many diagram groups belong to one documentation

### Validation Rules

- `Name` must be non-empty and max 200 characters
- `TableNames` must be valid JSON array
- `SortOrder` must be >= 0
- `AiConfidenceScore` must be between 0.00 and 1.00 if provided

### Group Management Logic

```csharp
public List<string> GetTableNames()
{
    return JsonSerializer.Deserialize<List<string>>(TableNames) ?? new List<string>();
}

public void AddTable(string tableName)
{
    var tables = GetTableNames();
    if (!tables.Contains(tableName))
    {
        tables.Add(tableName);
        TableNames = JsonSerializer.Serialize(tables);
        IsUserModified = true;
    }
}

public void RemoveTable(string tableName)
{
    var tables = GetTableNames();
    if (tables.Remove(tableName))
    {
        TableNames = JsonSerializer.Serialize(tables);
        IsUserModified = true;
    }
}
```

### Indexes

```sql
CREATE INDEX IX_DiagramGroup_DocumentationId ON DiagramGroup(DocumentationId);
CREATE INDEX IX_DiagramGroup_SortOrder ON DiagramGroup(SortOrder);
```

---

## 14. SchemaSnapshot

**Purpose**: Stores complete schema state at documentation generation time for comparison during change detection.

**Lifecycle**: Created when documentation is generated or regenerated, updated when user acknowledges changes and regenerates.

### Fields

| Field | Type | Nullable | Description |
|-------|------|----------|-------------|
| `Id` | int | No | Primary key |
| `DocumentationId` | int | No | Foreign key to DataSourceDocumentation |
| `SnapshotJson` | string | No | JSON: complete schema (tables, columns, types, relationships) |
| `TableCount` | int | No | Number of tables in snapshot |
| `ColumnCount` | int | No | Total columns across all tables |
| `RelationshipCount` | int | No | Number of foreign key relationships |
| `SchemaHash` | string(64) | No | SHA-256 hash of schema for quick comparison |
| `CapturedAt` | DateTime | No | When snapshot was taken |
| `CapturedByUserId` | int | No | User who triggered documentation generation |
| `CreatedAt` | DateTime | No | Record creation timestamp |
| `ModifiedAt` | DateTime | No | Record modification timestamp |

### Relationships

- **DataSourceDocumentation** (1:1): One snapshot per documentation version

### Validation Rules

- `DocumentationId` must reference existing documentation
- `SnapshotJson` must be valid JSON
- `SchemaHash` must be valid SHA-256 hex string (64 characters)
- `TableCount` must be >= 0
- `ColumnCount` must be >= 0
- `RelationshipCount` must be >= 0

### Schema JSON Structure

```json
{
  "tables": [
    {
      "name": "orders",
      "columns": [
        { "name": "id", "type": "int", "nullable": false, "isPrimaryKey": true },
        { "name": "customer_id", "type": "int", "nullable": false, "isForeignKey": true }
      ],
      "primaryKey": ["id"],
      "foreignKeys": [
        { "columns": ["customer_id"], "referencedTable": "customers", "referencedColumns": ["id"] }
      ]
    }
  ],
  "capturedAt": "2026-01-04T12:00:00Z"
}
```

### Indexes

```sql
CREATE UNIQUE INDEX IX_SchemaSnapshot_DocumentationId ON SchemaSnapshot(DocumentationId);
CREATE INDEX IX_SchemaSnapshot_CapturedAt ON SchemaSnapshot(CapturedAt DESC);
```

---

## 15. SchemaChange

**Purpose**: Records individual detected schema changes with timestamps, change details, and AI rename suggestions for audit trail.

**Lifecycle**: Created when schema change is detected, updated when user confirms/rejects rename suggestion, retained indefinitely for audit.

### Fields

| Field | Type | Nullable | Description |
|-------|------|----------|-------------|
| `Id` | int | No | Primary key |
| `DocumentationId` | int | No | Foreign key to DataSourceDocumentation |
| `DetectedAt` | DateTime | No | When change was detected |
| `ChangeType` | SchemaChangeType | No | TableAdded, TableRemoved, TableRenamed, ColumnAdded, etc. |
| `ObjectType` | SchemaObjectType | No | Table, Column, Relationship |
| `ObjectName` | string(500) | No | Full name (e.g., "orders.customer_id" for column) |
| `PreviousValue` | string | Yes | JSON: previous state (null for additions) |
| `CurrentValue` | string | Yes | JSON: current state (null for removals) |
| `IsRename` | bool | No | True if AI detected this as a rename |
| `RenameFromName` | string(500) | Yes | Original name if rename detected |
| `RenameToName` | string(500) | Yes | New name if rename detected |
| `RenameConfidenceScore` | decimal(3,2) | Yes | AI confidence in rename detection (0.00-1.00) |
| `RenameStatus` | RenameStatus | Yes | Pending, Confirmed, Rejected (null if not a rename) |
| `RenameConfirmedByUserId` | int | Yes | User who confirmed/rejected rename |
| `RenameConfirmedAt` | DateTime | Yes | When rename was confirmed/rejected |
| `IsAcknowledged` | bool | No | True if user has seen this change |
| `AcknowledgedByUserId` | int | Yes | User who acknowledged |
| `AcknowledgedAt` | DateTime | Yes | When acknowledged |
| `CreatedAt` | DateTime | No | Record creation timestamp |
| `ModifiedAt` | DateTime | No | Record modification timestamp |

### Relationships

- **DataSourceDocumentation** (N:1): Many changes belong to one documentation

### Validation Rules

- `ObjectName` must be non-empty
- If `IsRename` = true, `RenameFromName` and `RenameToName` must be provided
- If `RenameStatus` is Confirmed or Rejected, `RenameConfirmedByUserId` and `RenameConfirmedAt` must be provided
- `RenameConfidenceScore` must be between 0.00 and 1.00 if provided

### Change Detection Logic

```csharp
public static List<SchemaChange> DetectChanges(SchemaSnapshot previous, SchemaSnapshot current)
{
    var changes = new List<SchemaChange>();
    var prevSchema = JsonSerializer.Deserialize<SchemaData>(previous.SnapshotJson);
    var currSchema = JsonSerializer.Deserialize<SchemaData>(current.SnapshotJson);

    // Detect table additions/removals
    var prevTables = prevSchema.Tables.Select(t => t.Name).ToHashSet();
    var currTables = currSchema.Tables.Select(t => t.Name).ToHashSet();

    foreach (var added in currTables.Except(prevTables))
        changes.Add(new SchemaChange { ChangeType = SchemaChangeType.TableAdded, ObjectName = added });

    foreach (var removed in prevTables.Except(currTables))
        changes.Add(new SchemaChange { ChangeType = SchemaChangeType.TableRemoved, ObjectName = removed });

    // Detect column and relationship changes for existing tables...
    // AI rename detection handled separately

    return changes;
}
```

### Indexes

```sql
CREATE INDEX IX_SchemaChange_DocumentationId ON SchemaChange(DocumentationId);
CREATE INDEX IX_SchemaChange_DetectedAt ON SchemaChange(DetectedAt DESC);
CREATE INDEX IX_SchemaChange_ChangeType ON SchemaChange(ChangeType);
CREATE INDEX IX_SchemaChange_IsAcknowledged ON SchemaChange(IsAcknowledged);
CREATE INDEX IX_SchemaChange_RenameStatus ON SchemaChange(RenameStatus);
```

---

## 16. PromptTemplateVersion

**Purpose**: Stores version history for the documentation generation system prompt, enabling rollback and change tracking.

**Lifecycle**: Created when user explicitly creates a new version via "Create Version" button, retained indefinitely.

### Fields

| Field | Type | Nullable | Description |
|-------|------|----------|-------------|
| `Id` | int | No | Primary key |
| `PromptTemplateId` | int | No | Foreign key to AiPromptTemplate (documentation generation prompt) |
| `VersionNumber` | int | No | Sequential version number (1, 2, 3...) |
| `PromptContent` | string | No | Full prompt content at this version |
| `SystemPromptContent` | string | Yes | System prompt content at this version (if applicable) |
| `CreatedAt` | DateTime | No | When version was created |
| `CreatedByUserId` | int | No | User who created this version |
| `IsActive` | bool | No | Whether this is the currently active version |

### Relationships

- **AiPromptTemplate** (N:1): Many versions belong to one prompt template

### Validation Rules

- `PromptTemplateId` must reference existing AiPromptTemplate
- `VersionNumber` must be > 0 and sequential within the same PromptTemplateId
- `PromptContent` must be non-empty
- Only one version per PromptTemplateId can have `IsActive = true`

### Rollback Logic

```csharp
public static PromptTemplateVersion CreateFromRollback(PromptTemplateVersion source, int newVersionNumber, int userId)
{
    return new PromptTemplateVersion
    {
        PromptTemplateId = source.PromptTemplateId,
        VersionNumber = newVersionNumber,
        PromptContent = source.PromptContent,
        SystemPromptContent = source.SystemPromptContent,
        CreatedAt = DateTime.UtcNow,
        CreatedByUserId = userId,
        IsActive = true
    };
}
```

### Indexes

```sql
CREATE INDEX IX_PromptTemplateVersion_PromptTemplateId ON PromptTemplateVersion(PromptTemplateId);
CREATE INDEX IX_PromptTemplateVersion_VersionNumber ON PromptTemplateVersion(VersionNumber DESC);
CREATE INDEX IX_PromptTemplateVersion_IsActive ON PromptTemplateVersion(IsActive);
```

---

## Enumerations

### DocumentationStatus
```csharp
public enum DocumentationStatus
{
    Draft = 0,      // Initial state, not published
    Published = 1,  // Published and visible
    Archived = 2    // Archived, hidden
}
```

### SectionType
```csharp
public enum SectionType
{
    Overview = 0,           // High-level database description
    TableDescription = 1,   // Individual table description
    ColumnDescription = 2,  // Individual column description
    Relationships = 3,      // Foreign key relationships
    DataQuality = 4,        // Data quality observations
    SampleData = 5          // Sample data examples
}
```

### ContentFormat
```csharp
public enum ContentFormat
{
    Markdown = 0,
    PlainText = 1,
    Html = 2
}
```

### AlertStatus
```csharp
public enum AlertStatus
{
    Draft = 0,    // Being configured
    Active = 1,   // Running and sending alerts
    Paused = 2,   // Temporarily disabled
    Archived = 3  // Deleted
}
```

### ConversationRole
```csharp
public enum ConversationRole
{
    System = 0,    // System instructions
    User = 1,      // User messages
    Assistant = 2  // AI responses
}
```

### OperationType
```csharp
public enum OperationType
{
    SchemaAnalysis = 0,
    QueryGeneration = 1,
    QueryRefinement = 2,
    Documentation = 3,
    Validation = 4,
    ErrorFixing = 5,
    FieldAnalysis = 6
}
```

### FieldUsageStatus
```csharp
public enum FieldUsageStatus
{
    Used = 0,              // >=1% non-null values
    PotentiallyUnused = 1, // <1% non-null values (but not zero)
    Unused = 2             // 0% non-null values (completely empty)
}
```

### DetectedDataPattern
```csharp
public enum DetectedDataPattern
{
    None = 0,       // No specific pattern detected
    Email = 1,      // Email addresses (e.g., user@domain.com)
    Phone = 2,      // Phone numbers (various formats)
    Date = 3,       // Date/time strings in text columns
    Json = 4,       // JSON objects/arrays stored as text
    Url = 5,        // URLs/URIs
    Numeric = 6,    // Numeric values stored as strings
    Uuid = 7,       // UUIDs/GUIDs stored as strings
    Mixed = 8       // Multiple patterns detected (>1 pattern >20% each)
}
```

### MonitoringMode
```csharp
public enum MonitoringMode
{
    TaskMode = 0,         // AI creates draft alerts for user review
    NotificationMode = 1  // AI sends notifications directly
}
```

### MonitoringScheduleFrequency
```csharp
public enum MonitoringScheduleFrequency
{
    Hourly = 0,    // Run every hour
    Daily = 1,     // Run once per day
    Weekly = 2,    // Run once per week
    Custom = 3     // Use custom cron expression
}
```

### VerbosityLevel
```csharp
public enum VerbosityLevel
{
    Minimal = 0,   // Just the finding (e.g., "Sales dropped 25%")
    Standard = 1,  // Finding + comparison data
    Detailed = 2,  // Finding + comparison + potential causes
    Full = 3       // All above + suggested query + recommended action
}
```

### AnomalyType
```csharp
public enum AnomalyType
{
    Statistical = 0,      // Value outside 2-3 standard deviations
    TrendChange = 1,      // Growth stops, reverses, or accelerates
    MissingData = 2,      // Table stopped receiving updates, gaps in time series
    ThresholdBreach = 3,  // Value exceeds AI-learned "normal" range
    VolumeAnomaly = 4,    // Sudden spike or drop in row counts
    CorrelationBreak = 5  // Metrics that usually move together start diverging
}
```

### InsightSeverity
```csharp
public enum InsightSeverity
{
    Low = 0,       // Minor deviation, likely noise
    Medium = 1,    // Notable deviation, worth reviewing
    High = 2,      // Significant deviation, needs attention
    Critical = 3   // Major deviation, immediate action recommended
}
```

### InsightStatus
```csharp
public enum InsightStatus
{
    New = 0,              // Just detected, not yet reviewed
    Reviewed = 1,         // User has reviewed but taken no action
    Dismissed = 2,        // User marked as not relevant
    ConvertedToAlert = 3  // User created ongoing alert from this insight
}
```

### BaselineType
```csharp
public enum BaselineType
{
    Statistical = 0,  // Mean, std deviation based
    Trend = 1,        // Directional trend based
    Threshold = 2,    // Fixed or dynamic threshold based
    Volume = 3        // Row count/volume based
}
```

### TrendDirection
```csharp
public enum TrendDirection
{
    Stable = 0,      // No significant trend
    Increasing = 1,  // Upward trend
    Decreasing = 2,  // Downward trend
    Volatile = 3     // High variance, no clear direction
}
```

### ExportFormat
```csharp
public enum ExportFormat
{
    Html = 0,       // Interactive HTML with Mermaid diagrams
    Pdf = 1,        // Static PDF (existing)
    Markdown = 2,   // Markdown text (existing)
    Json = 3        // JSON for programmatic access (existing)
}
```

### DiagramGroupingCriteria
```csharp
public enum DiagramGroupingCriteria
{
    ForeignKey = 0,  // Grouped by foreign key relationships
    Naming = 1,      // Grouped by naming conventions (common prefixes)
    Semantic = 2,    // Grouped by AI semantic analysis of documentation
    Manual = 3       // User-created manual grouping
}
```

### SchemaChangeType
```csharp
public enum SchemaChangeType
{
    TableAdded = 0,
    TableRemoved = 1,
    TableRenamed = 2,
    ColumnAdded = 3,
    ColumnRemoved = 4,
    ColumnRenamed = 5,
    ColumnTypeChanged = 6,
    RelationshipAdded = 7,
    RelationshipRemoved = 8
}
```

### SchemaObjectType
```csharp
public enum SchemaObjectType
{
    Table = 0,
    Column = 1,
    Relationship = 2
}
```

### RenameStatus
```csharp
public enum RenameStatus
{
    Pending = 0,    // AI suggested rename, awaiting user confirmation
    Confirmed = 1,  // User confirmed this is a rename
    Rejected = 2    // User rejected - treat as delete + add
}
```

---

## Migration Strategy

### Migration 1: Core Tables
```sql
-- DataSourceDocumentation
-- DocumentationSection
-- DocumentationVersion
-- FieldAnalysis
-- DocumentationExport
-- DiagramGroup
-- SchemaSnapshot
-- SchemaChange
```

### Migration 2: Alert Configuration
```sql
-- AiAlertConfiguration
-- AiConversationHistory
```

### Migration 3: Metrics and Templates
```sql
-- AiUsageMetrics
-- AiPromptTemplate
```

### Migration 4: AI Monitoring
```sql
-- AiMonitoringConfiguration
-- AiMonitoringBaseline
-- AiInsight
-- ALTER AiAlertConfiguration ADD IsAiGenerated BIT, AiInsightId INT
```

### Data Retention

- **AiUsageMetrics**: Retain detailed records for 90 days, then aggregate by month
- **AiConversationHistory**: Retain for 1 year for learning purposes
- **DocumentationVersion**: Retain last 10 versions per documentation
- **AiInsight**: Retain for 90 days (dismissed insights), 1 year (converted to alerts)
- **AiMonitoringBaseline**: Continuously updated, no automatic cleanup
- **All others**: No automatic cleanup (user-controlled archival)

---

## Entity Implementation Examples

### DataSourceDocumentation.cs
```csharp
public class DataSourceDocumentation : BaseArchivableEntity, IChangeableEntity
{
    public int DataSourceId { get; set; }
    public string Title { get; set; } = null!;
    public string GeneratedByModel { get; set; } = null!;
    public DateTime GeneratedAt { get; set; }
    public int GeneratedByUserId { get; set; }
    public int? LastModifiedByUserId { get; set; }
    public DateTime? LastModifiedAt { get; set; }
    public DocumentationStatus Status { get; set; }
    public int TablesAnalyzed { get; set; }
    public int TokensUsed { get; set; }
    public decimal EstimatedCost { get; set; }
    public string? Metadata { get; set; }

    // Navigation properties
    public DataSource DataSource { get; set; } = null!;
    public ICollection<DocumentationSection> Sections { get; set; } = new List<DocumentationSection>();
    public ICollection<DocumentationVersion> Versions { get; set; } = new List<DocumentationVersion>();
}
```

### AiAlertConfiguration.cs
```csharp
public class AiAlertConfiguration : BaseArchivableEntity, IChangeableEntity
{
    public int DataSourceId { get; set; }
    public string Name { get; set; } = null!;
    public string NaturalLanguageDescription { get; set; } = null!;
    public string GeneratedSql { get; set; } = null!;
    public string? FinalSql { get; set; }
    public string GeneratedByModel { get; set; } = null!;
    public string? GenerationReasoning { get; set; }
    public decimal? ConfidenceScore { get; set; }
    public AlertStatus Status { get; set; }
    public string? ValidationErrors { get; set; }
    public string? UserFeedback { get; set; }
    public int? SubscriptionId { get; set; }
    public int ConversationTurns { get; set; }
    public int TokensUsed { get; set; }
    public decimal EstimatedCost { get; set; }

    // AI Monitoring fields (added for unsupervised monitoring)
    public bool IsAiGenerated { get; set; }  // True if created by AI monitoring (not user)
    public int? AiInsightId { get; set; }    // Source insight if created from AI monitoring

    // Navigation properties
    public DataSource DataSource { get; set; } = null!;
    public Subscription? Subscription { get; set; }
    public AiInsight? SourceInsight { get; set; }
    public ICollection<AiConversationHistory> ConversationHistory { get; set; } = new List<AiConversationHistory>();
}
```

---

### FieldAnalysis.cs
```csharp
public class FieldAnalysis : IChangeableEntity
{
    public int Id { get; set; }
    public int DocumentationId { get; set; }
    public string TableName { get; set; } = null!;
    public string ColumnName { get; set; } = null!;
    public string DataType { get; set; } = null!;
    public long TotalRows { get; set; }
    public long SampledRows { get; set; }
    public bool UsedFullScan { get; set; }
    public long NonNullCount { get; set; }
    public decimal NullPercentage { get; set; }
    public long DistinctValues { get; set; }
    public FieldUsageStatus UsageStatus { get; set; }
    public DetectedDataPattern? DetectedPattern { get; set; }
    public decimal? PatternMatchPercentage { get; set; }
    public string? SuggestedDataType { get; set; }
    public decimal? MigrationFeasibility { get; set; }
    public int? MigrationIssueCount { get; set; }
    public string? AiRecommendation { get; set; }
    public decimal? AiConfidenceScore { get; set; }
    public string? SampleValues { get; set; }
    public DateTime AnalyzedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
    public string CreatedBy { get; set; } = null!;
    public string ModifiedBy { get; set; } = null!;

    // Navigation properties
    public DataSourceDocumentation Documentation { get; set; } = null!;
}
```

---

### AiMonitoringConfiguration.cs
```csharp
public class AiMonitoringConfiguration : BaseArchivableEntity, IChangeableEntity
{
    public int DataSourceId { get; set; }
    public bool IsEnabled { get; set; }
    public MonitoringMode MonitoringMode { get; set; }
    public MonitoringScheduleFrequency ScheduleFrequency { get; set; }
    public string? ScheduleCron { get; set; }
    public bool AllowAdaptiveFrequency { get; set; }
    public int BaselinePeriodDays { get; set; } = 30;
    public VerbosityLevel VerbosityLevel { get; set; }
    public int MaxQueriesPerDay { get; set; } = 100;
    public int MaxTokensPerDay { get; set; } = 100_000;
    public decimal MaxCostPerMonth { get; set; } = 10.00m;
    public int CurrentDayQueries { get; set; }
    public int CurrentDayTokens { get; set; }
    public decimal CurrentMonthCost { get; set; }
    public DateTime LastResetDate { get; set; }
    public DateTime LastMonthlyResetDate { get; set; }
    public DateTime? LastRunAt { get; set; }
    public DateTime? NextScheduledRunAt { get; set; }
    public bool IsPausedDueToLimits { get; set; }

    // Navigation properties
    public DataSource DataSource { get; set; } = null!;
    public ICollection<AiMonitoringBaseline> Baselines { get; set; } = new List<AiMonitoringBaseline>();
    public ICollection<AiInsight> Insights { get; set; } = new List<AiInsight>();
}
```

### AiInsight.cs
```csharp
public class AiInsight : IChangeableEntity
{
    public int Id { get; set; }
    public int MonitoringConfigId { get; set; }
    public AnomalyType AnomalyType { get; set; }
    public InsightSeverity Severity { get; set; }
    public InsightStatus Status { get; set; }
    public string MetricName { get; set; } = null!;
    public decimal? CurrentValue { get; set; }
    public decimal? ExpectedValue { get; set; }
    public decimal? DeviationPercentage { get; set; }
    public string Title { get; set; } = null!;
    public string Description { get; set; } = null!;
    public string? SuggestedAction { get; set; }
    public string? SuggestedQuery { get; set; }
    public string? RelatedData { get; set; }
    public DateTime DetectedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public int? ReviewedByUserId { get; set; }
    public int? DraftAlertId { get; set; }
    public decimal? AiConfidenceScore { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }

    // Navigation properties
    public AiMonitoringConfiguration MonitoringConfig { get; set; } = null!;
    public AiAlertConfiguration? DraftAlert { get; set; }
}
```

---

## Summary

**Total New Entities**: 16
- 8 for Documentation (DataSourceDocumentation, DocumentationSection, DocumentationVersion, FieldAnalysis, DocumentationExport, DiagramGroup, SchemaSnapshot, SchemaChange)
- 2 for AI Alerts (AiAlertConfiguration, AiConversationHistory)
- 3 for AI Monitoring (AiMonitoringConfiguration, AiMonitoringBaseline, AiInsight)
- 3 for Infrastructure (AiUsageMetrics, AiPromptTemplate, PromptTemplateVersion)

**Total New Enums**: 22
- Documentation: DocumentationStatus, SectionType, ContentFormat, ExportFormat, DiagramGroupingCriteria
- Schema Change: SchemaChangeType, SchemaObjectType, RenameStatus
- Field Analysis: FieldUsageStatus, DetectedDataPattern
- Alerts: AlertStatus, ConversationRole, OperationType
- AI Monitoring: MonitoringMode, MonitoringScheduleFrequency, VerbosityLevel, AnomalyType, InsightSeverity, InsightStatus, BaselineType, TrendDirection

**Database Impact**:
- 16 new tables
- 50 new indexes
- Schema-agnostic (works with existing PostgreSQL/SQL Server migrations)
- Follows existing BaseArchivableEntity and IChangeableEntity patterns

**Next Step**: Generate API contracts for MediatR handlers that will operate on these entities.

# API Contracts: Admin & Monitoring Operations

**Feature**: 005-ai-integration
**Category**: Usage tracking, cost monitoring, and AI configuration

---

## 1. Get AI Usage Metrics

**Operation**: Retrieve AI usage statistics for cost monitoring and optimization.

### Query: GetAiUsageMetricsQuery

```csharp
public record GetAiUsageMetricsQuery : IRequest<AiUsageMetricsResult>
{
    public DateTime? StartDate { get; init; }
    public DateTime? EndDate { get; init; }
    public int? UserId { get; init; }
    public int? DataSourceId { get; init; }
    public string? Provider { get; init; }
    public OperationType? OperationType { get; init; }
    public MetricsAggregation Aggregation { get; init; } = MetricsAggregation.Daily;
}

public enum MetricsAggregation
{
    Hourly,
    Daily,
    Weekly,
    Monthly
}
```

### Response: AiUsageMetricsResult

```csharp
public record AiUsageMetricsResult
{
    public List<UsageDataPoint> DataPoints { get; init; } = new();
    public UsageSummary Summary { get; init; } = null!;
}

public record UsageDataPoint
{
    public DateTime Period { get; init; }
    public int RequestCount { get; init; }
    public int TotalTokens { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public decimal TotalCost { get; init; }
    public int SuccessCount { get; init; }
    public int ErrorCount { get; init; }
    public int AvgResponseTimeMs { get; init; }
}

public record UsageSummary
{
    public int TotalRequests { get; init; }
    public long TotalTokens { get; init; }
    public decimal TotalCost { get; init; }
    public int CacheHitCount { get; init; }
    public decimal CacheHitRate { get; init; }
    public Dictionary<string, int> RequestsByProvider { get; init; } = new();
    public Dictionary<string, int> RequestsByOperation { get; init; } = new();
}
```

### Validation Rules

- If `StartDate` provided, `EndDate` must also be provided
- Date range cannot exceed 365 days
- Only admins can view usage for all users
- Regular users can only view their own usage

### Example Request

```json
{
  "startDate": "2026-01-01T00:00:00Z",
  "endDate": "2026-01-31T23:59:59Z",
  "userId": null,
  "provider": "Anthropic",
  "operationType": null,
  "aggregation": "Daily"
}
```

### Example Response

```json
{
  "dataPoints": [
    {
      "period": "2026-01-03T00:00:00Z",
      "requestCount": 45,
      "totalTokens": 127840,
      "inputTokens": 95200,
      "outputTokens": 32640,
      "totalCost": 0.775,
      "successCount": 43,
      "errorCount": 2,
      "avgResponseTimeMs": 3420
    }
  ],
  "summary": {
    "totalRequests": 1350,
    "totalTokens": 3845200,
    "totalCost": 23.25,
    "cacheHitCount": 245,
    "cacheHitRate": 0.18,
    "requestsByProvider": {
      "Anthropic": 890,
      "OpenAI": 460
    },
    "requestsByOperation": {
      "SchemaAnalysis": 150,
      "QueryGeneration": 800,
      "QueryRefinement": 320,
      "Documentation": 80
    }
  }
}
```

---

## 2. Configure AI Provider

**Operation**: Update AI provider configuration (admin only).

### Command: ConfigureAiProviderCommand

```csharp
public record ConfigureAiProviderCommand : IRequest<ConfigureAiProviderResult>
{
    public string Provider { get; init; } = null!;  // OpenAI, Anthropic, AzureOpenAI
    public string ApiKey { get; init; } = null!;
    public string? Endpoint { get; init; }  // For Azure OpenAI
    public string DefaultModel { get; init; } = null!;
    public string? FastModel { get; init; }  // For validation/simple tasks
    public ProviderLimits Limits { get; init; } = new();
    public bool IsEnabled { get; init; } = true;
}

public record ProviderLimits
{
    public int MaxConcurrentRequests { get; init; } = 50;
    public int TokensPerMinute { get; init; }
    public int RequestsPerMinute { get; init; }
    public decimal MonthlyBudget { get; init; }  // Cost limit
}
```

### Response: ConfigureAiProviderResult

```csharp
public record ConfigureAiProviderResult
{
    public string Provider { get; init; } = null!;
    public bool IsEnabled { get; init; }
    public DateTime ConfiguredAt { get; init; }
    public ProviderStatus Status { get; init; }
}

public enum ProviderStatus
{
    Configured,
    Testing,
    Active,
    RateLimited,
    BudgetExceeded,
    Error
}
```

### Validation Rules

- Only system administrators can configure providers
- ApiKey must be encrypted before storage
- Provider must be validated before activation (test API call)
- Monthly budget must be > 0 if specified

### Business Rules

1. API keys encrypted using system key vault
2. Provider availability tested immediately
3. If test fails, configuration saved but IsEnabled = false
4. Old API keys rotated securely (not deleted immediately)
5. Rate limits enforced via request queue

---

## 3. Update AI Prompt Template

**Operation**: Create or update prompt templates for different operations (admin only).

### Command: UpdatePromptTemplateCommand

```csharp
public record UpdatePromptTemplateCommand : IRequest<UpdatePromptTemplateResult>
{
    public int? TemplateId { get; init; }  // Null = create new
    public string Name { get; init; } = null!;
    public OperationType OperationType { get; init; }
    public string PromptTemplate { get; init; } = null!;
    public string? SystemPrompt { get; init; }
    public decimal Temperature { get; init; } = 0.3m;
    public int MaxTokens { get; init; } = 4096;
    public Dictionary<string, string> VariableDefinitions { get; init; } = new();
    public string? Description { get; init; }
    public bool SetAsActive { get; init; }
}
```

### Response: UpdatePromptTemplateResult

```csharp
public record UpdatePromptTemplateResult
{
    public int TemplateId { get; init; }
    public int Version { get; init; }
    public bool IsActive { get; init; }
    public DateTime UpdatedAt { get; init; }
}
```

### Validation Rules

- Only admins can update templates
- `PromptTemplate` must contain {{variables}} from VariableDefinitions
- `Temperature` must be between 0.0 and 2.0
- `MaxTokens` must be > 0 and <= provider limit
- If `SetAsActive` = true, previous active template for same OperationType is deactivated

### Example Request

```json
{
  "name": "Schema Analysis v2",
  "operationType": "SchemaAnalysis",
  "promptTemplate": "Analyze the following {{tableCount}} database tables...",
  "systemPrompt": "You are an expert database analyst...",
  "temperature": 0.3,
  "maxTokens": 4096,
  "variableDefinitions": {
    "tableCount": "integer",
    "schemaJson": "string",
    "sampleDataJson": "string"
  },
  "description": "Updated for better relationship detection",
  "setAsActive": true
}
```

---

## 4. Test AI Configuration

**Operation**: Test AI provider configuration and model availability.

### Query: TestAiConfigurationQuery

```csharp
public record TestAiConfigurationQuery : IRequest<TestAiConfigurationResult>
{
    public string Provider { get; init; } = null!;
    public string? Model { get; init; }  // If null, tests default model
    public bool IncludeLatencyTest { get; init; } = true;
}
```

### Response: TestAiConfigurationResult

```csharp
public record TestAiConfigurationResult
{
    public bool IsAvailable { get; init; }
    public bool IsConfigured { get; init; }
    public string? ErrorMessage { get; init; }
    public TestMetrics? Metrics { get; init; }
}

public record TestMetrics
{
    public int ResponseTimeMs { get; init; }
    public int TokensUsed { get; init; }
    public decimal EstimatedCost { get; init; }
    public string TestedModel { get; init; } = null!;
}
```

### Business Rules

1. Sends simple test prompt to provider
2. Measures response time and token usage
3. Does not count toward usage metrics
4. Admins can test any provider, users can test active provider only

---

## 5. Set Budget Alert

**Operation**: Configure budget alerts for AI usage.

### Command: SetBudgetAlertCommand

```csharp
public record SetBudgetAlertCommand : IRequest<SetBudgetAlertResult>
{
    public int? UserId { get; init; }  // Null = organization-wide
    public BudgetPeriod Period { get; init; }
    public decimal Threshold { get; init; }
    public List<string> NotificationEmails { get; init; } = new();
}

public enum BudgetPeriod
{
    Daily,
    Weekly,
    Monthly
}
```

### Response: SetBudgetAlertResult

```csharp
public record SetBudgetAlertResult
{
    public int AlertId { get; init; }
    public decimal CurrentSpend { get; init; }
    public decimal Threshold { get; init; }
    public decimal RemainingBudget { get; init; }
    public bool IsTriggered { get; init; }
}
```

### Validation Rules

- Only admins can set organization-wide alerts
- Users can set alerts for their own usage
- Threshold must be > 0
- At least one notification email required

### Business Rules

1. Alert triggers when spend >= threshold
2. Notifications sent once per period
3. Can configure warning thresholds (e.g., 80%, 90%, 100%)
4. Alert does not block requests (warning only)

---

## 6. Export Usage Report

**Operation**: Export detailed usage report for billing or auditing.

### Query: ExportUsageReportQuery

```csharp
public record ExportUsageReportQuery : IRequest<UsageReportExportResult>
{
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }
    public ReportFormat Format { get; init; }
    public ReportGrouping Grouping { get; init; }
    public bool IncludeDetailedRequests { get; init; }
}

public enum ReportFormat
{
    Csv,
    Excel,
    Json,
    Pdf
}

public enum ReportGrouping
{
    ByUser,
    ByDataSource,
    ByOperation,
    ByProvider,
    ByDay
}
```

### Response: UsageReportExportResult

```csharp
public record UsageReportExportResult
{
    public byte[] FileData { get; init; } = null!;
    public string ContentType { get; init; } = null!;
    public string FileName { get; init; } = null!;
    public long FileSizeBytes { get; init; }
}
```

### Business Rules

1. Only admins can export organization-wide reports
2. Users can export their own usage reports
3. Reports include: requests, tokens, costs, errors
4. If `IncludeDetailedRequests` = true, includes every API call (large files)

---

## 7. Get AI Provider Status

**Operation**: Check real-time status of configured AI providers.

### Query: GetAiProviderStatusQuery

```csharp
public record GetAiProviderStatusQuery : IRequest<AiProviderStatusResult>
{
    public string? Provider { get; init; }  // Null = all providers
}
```

### Response: AiProviderStatusResult

```csharp
public record AiProviderStatusResult
{
    public List<ProviderStatus> Providers { get; init; } = new();
}

public record ProviderStatus
{
    public string Provider { get; init; } = null!;
    public bool IsEnabled { get; init; }
    public bool IsAvailable { get; init; }
    public int CurrentConcurrentRequests { get; init; }
    public int QueuedRequests { get; init; }
    public decimal TodaySpend { get; init; }
    public decimal MonthlySpend { get; init; }
    public decimal BudgetRemaining { get; init; }
    public RateLimitStatus RateLimits { get; init; } = null!;
    public DateTime? LastError { get; init; }
    public string? LastErrorMessage { get; init; }
}

public record RateLimitStatus
{
    public int TokensPerMinuteLimit { get; init; }
    public int TokensPerMinuteUsed { get; init; }
    public int RequestsPerMinuteLimit { get; init; }
    public int RequestsPerMinuteUsed { get; init; }
    public int SecondsUntilReset { get; init; }
}
```

### Example Response

```json
{
  "providers": [
    {
      "provider": "Anthropic",
      "isEnabled": true,
      "isAvailable": true,
      "currentConcurrentRequests": 8,
      "queuedRequests": 2,
      "todaySpend": 0.85,
      "monthlySpend": 23.40,
      "budgetRemaining": 76.60,
      "rateLimits": {
        "tokensPerMinuteLimit": 80000,
        "tokensPerMinuteUsed": 12450,
        "requestsPerMinuteLimit": 1000,
        "requestsPerMinuteUsed": 15,
        "secondsUntilReset": 42
      },
      "lastError": null,
      "lastErrorMessage": null
    }
  ]
}
```

---

## 8. Clear AI Cache

**Operation**: Clear cached prompts and responses (admin only).

### Command: ClearAiCacheCommand

```csharp
public record ClearAiCacheCommand : IRequest<ClearAiCacheResult>
{
    public CacheClearScope Scope { get; init; }
    public int? DataSourceId { get; init; }  // For DataSource scope
}

public enum CacheClearScope
{
    All,              // Clear all cached data
    SchemaMetadata,   // Clear cached schema information
    DataSource        // Clear cache for specific data source
}
```

### Response: ClearAiCacheResult

```csharp
public record ClearAiCacheResult
{
    public int ItemsCleared { get; init; }
    public DateTime ClearedAt { get; init; }
}
```

### Business Rules

1. Only admins can clear cache
2. Clearing cache may temporarily increase costs (no cache hits)
3. Use when schema has significantly changed
4. Cache automatically expires after 10 minutes

---

## Summary

**Total Contracts**: 8
- 4 Commands (Configure Provider, Update Template, Set Budget Alert, Clear Cache)
- 4 Queries (Get Metrics, Test Configuration, Get Status, Export Report)

**Key Features**:
- Comprehensive usage tracking and cost monitoring
- Budget alerts and thresholds
- Provider configuration and testing
- Prompt template management
- Real-time status monitoring
- Usage report export in multiple formats
- Cache management

**Admin Operations**: Restricted to system administrators
**User Operations**: Limited to own usage data and active provider testing

**Security Considerations**:
- API keys encrypted at rest
- Access control enforced (admins vs users)
- Audit logging for all configuration changes
- Rate limiting and budget controls

**Monitoring Capabilities**:
- Real-time request tracking
- Cost accumulation by user/data source
- Cache hit rates
- Error rates and response times
- Provider availability status

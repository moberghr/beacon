# API Contracts: AI Alert Operations

**Feature**: 005-ai-integration
**Category**: Natural language alert configuration

---

## 1. Generate Alert Query

**Operation**: Generate SQL query from natural language description using AI.

### Command: GenerateAlertQueryCommand

```csharp
public record GenerateAlertQueryCommand : IRequest<GenerateAlertQueryResult>
{
    public int DataSourceId { get; init; }
    public string NaturalLanguageDescription { get; init; } = null!;
    public string? AlertName { get; init; }
    public AlertGenerationOptions Options { get; init; } = new();
}

public record AlertGenerationOptions
{
    public bool RequestClarification { get; init; } = true;  // Ask AI to identify ambiguities
    public bool IncludeReasoning { get; init; } = true;      // Get AI explanation
    public bool ValidateSyntax { get; init; } = true;        // Run SQL validation
    public int MaxTables { get; init; } = 15;                 // Schema filtering limit
}
```

### Response: GenerateAlertQueryResult

```csharp
public record GenerateAlertQueryResult
{
    public int AlertConfigId { get; init; }
    public string GeneratedSql { get; init; } = null!;
    public string? Reasoning { get; init; }
    public decimal? ConfidenceScore { get; init; }
    public List<string> ClarificationQuestions { get; init; } = new();
    public ValidationResult ValidationResult { get; init; } = null!;
    public int TokensUsed { get; init; }
    public decimal EstimatedCost { get; init; }
    public string GeneratedByModel { get; init; } = null!;
    public TimeSpan GenerationTime { get; init; }
}

public record ValidationResult
{
    public bool IsValid { get; init; }
    public List<string> Errors { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    public bool CanExecute { get; init; }
}
```

### Validation Rules

- `DataSourceId` must exist and be accessible by current user
- `NaturalLanguageDescription` must be min 10 characters, max 2000 characters
- `AlertName` must be unique for the data source (if provided)
- `MaxTables` must be between 1 and 50

### Error Responses

| Error Code | HTTP Status | Description |
|------------|-------------|-------------|
| `DATA_SOURCE_NOT_FOUND` | 404 | Data source does not exist |
| `DATA_SOURCE_UNAUTHORIZED` | 403 | User does not have access |
| `DESCRIPTION_TOO_SHORT` | 400 | Natural language too brief to understand |
| `DESCRIPTION_TOO_VAGUE` | 400 | AI cannot interpret intent |
| `LLM_SERVICE_UNAVAILABLE` | 503 | LLM API unavailable |
| `RATE_LIMIT_EXCEEDED` | 429 | Too many concurrent requests |
| `SQL_GENERATION_FAILED` | 500 | AI failed to generate valid SQL |

### Business Rules

1. Only users with "Execute" permission on data source can generate alerts
2. AI uses schema filtering to identify relevant tables from natural language
3. If confidence score < 0.7, return clarification questions
4. Generated SQL must be SELECT-only (no INSERT/UPDATE/DELETE/DROP)
5. If validation fails, AI attempts to fix automatically (max 2 retries)
6. Alert config is saved as Draft status initially

### Example Request

```json
{
  "dataSourceId": 42,
  "naturalLanguageDescription": "Alert me when sales drop more than 20% compared to the same day last week",
  "alertName": "Weekly Sales Drop Alert",
  "options": {
    "requestClarification": true,
    "includeReasoning": true,
    "validateSyntax": true,
    "maxTables": 15
  }
}
```

### Example Response - Success

```json
{
  "alertConfigId": 501,
  "generatedSql": "SELECT \n  CURRENT_DATE as alert_date,\n  SUM(total_amount) as today_sales,\n  (SELECT SUM(total_amount) \n   FROM sales \n   WHERE DATE(order_date) = CURRENT_DATE - INTERVAL '7 days') as last_week_sales,\n  ((SUM(total_amount) - \n    (SELECT SUM(total_amount) FROM sales WHERE DATE(order_date) = CURRENT_DATE - INTERVAL '7 days')) \n   / (SELECT SUM(total_amount) FROM sales WHERE DATE(order_date) = CURRENT_DATE - INTERVAL '7 days') * 100) as percent_change\nFROM sales\nWHERE DATE(order_date) = CURRENT_DATE\nHAVING percent_change < -20",
  "reasoning": "This query compares today's total sales with sales from exactly 7 days ago (same day of week). It calculates the percentage change and triggers the alert only when sales have dropped by more than 20% (negative change exceeding -20%).",
  "confidenceScore": 0.92,
  "clarificationQuestions": [],
  "validationResult": {
    "isValid": true,
    "errors": [],
    "warnings": [
      "Query uses subqueries which may have performance implications on large tables"
    ],
    "canExecute": true
  },
  "tokensUsed": 3240,
  "estimatedCost": 0.012,
  "generatedByModel": "claude-sonnet-4.5",
  "generationTime": "00:00:04.2"
}
```

### Example Response - Clarification Needed

```json
{
  "alertConfigId": 502,
  "generatedSql": "",
  "reasoning": "I need clarification to generate an accurate query.",
  "confidenceScore": 0.55,
  "clarificationQuestions": [
    "Should I compare sales to the entire week or just the same day of the week?",
    "Do you want to include refunds in the calculation, or only completed sales?",
    "Should the alert trigger once per day, or continuously throughout the day?"
  ],
  "validationResult": {
    "isValid": false,
    "errors": [],
    "warnings": [],
    "canExecute": false
  },
  "tokensUsed": 1850,
  "estimatedCost": 0.006,
  "generatedByModel": "claude-sonnet-4.5",
  "generationTime": "00:00:02.8"
}
```

---

## 2. Refine Alert Query

**Operation**: Refine AI-generated query through conversational feedback.

### Command: RefineAlertQueryCommand

```csharp
public record RefineAlertQueryCommand : IRequest<RefineAlertQueryResult>
{
    public int AlertConfigId { get; init; }
    public string UserFeedback { get; init; } = null!;
    public bool ValidateAfterRefinement { get; init; } = true;
}
```

### Response: RefineAlertQueryResult

```csharp
public record RefineAlertQueryResult
{
    public int AlertConfigId { get; init; }
    public string RefinedSql { get; init; } = null!;
    public string? Reasoning { get; init; }
    public decimal? ConfidenceScore { get; init; }
    public ValidationResult ValidationResult { get; init; } = null!;
    public int ConversationTurn { get; init; }
    public int TokensUsed { get; init; }
    public decimal EstimatedCost { get; init; }
}
```

### Validation Rules

- `AlertConfigId` must exist and be owned by current user
- `UserFeedback` must be min 5 characters, max 2000 characters
- Cannot refine Active or Archived alerts
- Maximum 10 refinement turns per alert config

### Error Responses

| Error Code | HTTP Status | Description |
|------------|-------------|-------------|
| `ALERT_CONFIG_NOT_FOUND` | 404 | Alert configuration does not exist |
| `ALERT_CONFIG_UNAUTHORIZED` | 403 | User does not own this alert |
| `ALERT_CONFIG_ACTIVE` | 409 | Cannot refine active alerts |
| `MAX_REFINEMENTS_EXCEEDED` | 400 | Too many refinement iterations |
| `FEEDBACK_TOO_VAGUE` | 400 | AI cannot understand feedback |

### Business Rules

1. Each refinement adds a turn to conversation history
2. AI has context of all previous turns
3. Refinement uses fast model (GPT-4o mini) for cost efficiency
4. If validation still fails after refinement, offer manual SQL edit
5. User can revert to any previous conversation turn

### Example Request

```json
{
  "alertConfigId": 501,
  "userFeedback": "Actually, I want to compare to the same day last week, not just 7 days ago. And include only completed orders, not pending ones.",
  "validateAfterRefinement": true
}
```

### Example Response

```json
{
  "alertConfigId": 501,
  "refinedSql": "SELECT \n  CURRENT_DATE as alert_date,\n  SUM(total_amount) as today_sales,\n  (SELECT SUM(total_amount) \n   FROM sales \n   WHERE DATE(order_date) = DATE_TRUNC('week', CURRENT_DATE) - INTERVAL '7 days' + (EXTRACT(DOW FROM CURRENT_DATE) * INTERVAL '1 day')\n   AND status = 'completed') as last_week_sales,\n  ((SUM(total_amount) - \n    (SELECT SUM(total_amount) FROM sales WHERE DATE(order_date) = DATE_TRUNC('week', CURRENT_DATE) - INTERVAL '7 days' + (EXTRACT(DOW FROM CURRENT_DATE) * INTERVAL '1 day') AND status = 'completed')) \n   / (SELECT SUM(total_amount) FROM sales WHERE DATE(order_date) = DATE_TRUNC('week', CURRENT_DATE) - INTERVAL '7 days' + (EXTRACT(DOW FROM CURRENT_DATE) * INTERVAL '1 day') AND status = 'completed') * 100) as percent_change\nFROM sales\nWHERE DATE(order_date) = CURRENT_DATE\nAND status = 'completed'\nHAVING percent_change < -20",
  "reasoning": "Updated to compare same day of the week (e.g., Monday to Monday) and filter for completed orders only by adding status = 'completed' condition.",
  "confidenceScore": 0.95,
  "validationResult": {
    "isValid": true,
    "errors": [],
    "warnings": [],
    "canExecute": true
  },
  "conversationTurn": 2,
  "tokensUsed": 1420,
  "estimatedCost": 0.0008,
  "generatedByModel": "gpt-4o-mini"
}
```

---

## 3. Activate AI Alert

**Operation**: Validate and activate AI-generated alert by creating Subscription.

### Command: ActivateAiAlertCommand

```csharp
public record ActivateAiAlertCommand : IRequest<ActivateAiAlertResult>
{
    public int AlertConfigId { get; init; }
    public string? FinalSql { get; init; }  // Optional: user can override AI SQL
    public SubscriptionConfig SubscriptionConfig { get; init; } = null!;
}

public record SubscriptionConfig
{
    public string CronExpression { get; init; } = null!;
    public List<RecipientConfig> Recipients { get; init; } = new();
    public bool EnableNotifications { get; init; } = true;
}

public record RecipientConfig
{
    public int? RecipientId { get; init; }  // Existing recipient
    public string? Email { get; init; }      // Or create new email recipient
    public NotificationType Type { get; init; }
}
```

### Response: ActivateAiAlertResult

```csharp
public record ActivateAiAlertResult
{
    public int AlertConfigId { get; init; }
    public int SubscriptionId { get; init; }
    public string AlertName { get; init; } = null!;
    public DateTime ActivatedAt { get; init; }
    public DateTime NextRunTime { get; init; }
}
```

### Validation Rules

- `AlertConfigId` must be in Draft status
- If `FinalSql` provided, must pass validation
- `SubscriptionConfig.CronExpression` must be valid cron syntax
- At least one recipient must be specified
- User must have "Execute" permission on data source

### Error Responses

| Error Code | HTTP Status | Description |
|------------|-------------|-------------|
| `ALERT_CONFIG_NOT_FOUND` | 404 | Alert configuration does not exist |
| `ALERT_CONFIG_UNAUTHORIZED` | 403 | User does not own this alert |
| `SQL_VALIDATION_FAILED` | 400 | SQL query has syntax errors |
| `INVALID_CRON_EXPRESSION` | 400 | Cron expression is invalid |
| `NO_RECIPIENTS_SPECIFIED` | 400 | At least one recipient required |

### Business Rules

1. Creates Subscription entity linked to AlertConfig
2. AlertConfig status changes to Active
3. Subscription inherits query from AlertConfig.FinalSql or AlertConfig.GeneratedSql
4. First execution scheduled based on cron expression
5. Notification channels use existing adapter infrastructure (Email, Teams, Slack, Jira)

### Example Request

```json
{
  "alertConfigId": 501,
  "subscriptionConfig": {
    "cronExpression": "0 9 * * *",
    "recipients": [
      {
        "recipientId": 25
      },
      {
        "email": "sales-team@company.com",
        "type": "Email"
      }
    ],
    "enableNotifications": true
  }
}
```

### Example Response

```json
{
  "alertConfigId": 501,
  "subscriptionId": 1042,
  "alertName": "Weekly Sales Drop Alert",
  "activatedAt": "2026-01-03T19:15:00Z",
  "nextRunTime": "2026-01-04T09:00:00Z"
}
```

---

## 4. Pause AI Alert

**Operation**: Temporarily pause active AI alert.

### Command: PauseAiAlertCommand

```csharp
public record PauseAiAlertCommand : IRequest<PauseAiAlertResult>
{
    public int AlertConfigId { get; init; }
    public string? Reason { get; init; }
}
```

### Response: PauseAiAlertResult

```csharp
public record PauseAiAlertResult
{
    public int AlertConfigId { get; init; }
    public AlertStatus Status { get; init; }
    public DateTime PausedAt { get; init; }
}
```

### Business Rules

1. Changes AlertConfig.Status to Paused
2. Pauses linked Subscription
3. No notifications sent while paused
4. Can be resumed later

---

## 5. Resume AI Alert

**Operation**: Resume paused AI alert.

### Command: ResumeAiAlertCommand

```csharp
public record ResumeAiAlertCommand : IRequest<ResumeAiAlertResult>
{
    public int AlertConfigId { get; init; }
}
```

### Response: ResumeAiAlertResult

```csharp
public record ResumeAiAlertResult
{
    public int AlertConfigId { get; init; }
    public AlertStatus Status { get; init; }
    public DateTime ResumedAt { get; init; }
    public DateTime NextRunTime { get; init; }
}
```

---

## 6. Provide Alert Feedback

**Operation**: Submit feedback on AI-generated alert for learning purposes.

### Command: ProvideAlertFeedbackCommand

```csharp
public record ProvideAlertFeedbackCommand : IRequest<Unit>
{
    public int AlertConfigId { get; init; }
    public FeedbackType Type { get; init; }
    public string? Comments { get; init; }
    public int? Rating { get; init; }  // 1-5 stars
}

public enum FeedbackType
{
    QueryAccurate,
    QueryInaccurate,
    QuerySuboptimal,
    FalsePositive,
    FalseNegative,
    PerformanceIssue
}
```

### Business Rules

1. Feedback stored in AlertConfig.UserFeedback
2. Used for future AI improvements
3. No immediate impact on alert behavior
4. Optional but encouraged

---

## 7. Get Conversation History

**Operation**: Retrieve full conversation history for an alert configuration.

### Query: GetAlertConversationQuery

```csharp
public record GetAlertConversationQuery : IRequest<AlertConversationResult>
{
    public int AlertConfigId { get; init; }
}
```

### Response: AlertConversationResult

```csharp
public record AlertConversationResult
{
    public int AlertConfigId { get; init; }
    public string AlertName { get; init; } = null!;
    public List<ConversationTurn> Turns { get; init; } = new();
    public int TotalTokensUsed { get; init; }
    public decimal TotalCost { get; init; }
}

public record ConversationTurn
{
    public int TurnNumber { get; init; }
    public ConversationRole Role { get; init; }
    public string Message { get; init; } = null!;
    public string? SqlSnapshot { get; init; }  // SQL at this turn
    public DateTime Timestamp { get; init; }
    public int TokensUsed { get; init; }
}
```

### Example Response

```json
{
  "alertConfigId": 501,
  "alertName": "Weekly Sales Drop Alert",
  "turns": [
    {
      "turnNumber": 1,
      "role": "User",
      "message": "Alert me when sales drop more than 20% compared to the same day last week",
      "timestamp": "2026-01-03T18:00:00Z",
      "tokensUsed": 0
    },
    {
      "turnNumber": 2,
      "role": "Assistant",
      "message": "I've generated a SQL query that compares today's sales...",
      "sqlSnapshot": "SELECT...",
      "timestamp": "2026-01-03T18:00:04Z",
      "tokensUsed": 3240
    },
    {
      "turnNumber": 3,
      "role": "User",
      "message": "Actually, I want to compare to the same day last week...",
      "timestamp": "2026-01-03T18:05:00Z",
      "tokensUsed": 0
    },
    {
      "turnNumber": 4,
      "role": "Assistant",
      "message": "Updated to compare same day of the week...",
      "sqlSnapshot": "SELECT...",
      "timestamp": "2026-01-03T18:05:03Z",
      "tokensUsed": 1420
    }
  ],
  "totalTokensUsed": 4660,
  "totalCost": 0.0128
}
```

---

## 8. List AI Alerts

**Operation**: Get all AI-generated alerts for a data source.

### Query: ListAiAlertsQuery

```csharp
public record ListAiAlertsQuery : IRequest<ListAiAlertsResult>
{
    public int? DataSourceId { get; init; }  // Optional: filter by data source
    public AlertStatus? Status { get; init; }  // Optional: filter by status
    public int PageNumber { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
```

### Response: ListAiAlertsResult

```csharp
public record ListAiAlertsResult
{
    public List<AiAlertSummary> Items { get; init; } = new();
    public int TotalCount { get; init; }
    public int PageNumber { get; init; }
    public int PageSize { get; init; }
}

public record AiAlertSummary
{
    public int Id { get; init; }
    public string Name { get; init; } = null!;
    public string DataSourceName { get; init; } = null!;
    public AlertStatus Status { get; init; }
    public string NaturalLanguageDescription { get; init; } = null!;
    public decimal? ConfidenceScore { get; init; }
    public int ConversationTurns { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ActivatedAt { get; init; }
    public int? SubscriptionId { get; init; }
}
```

---

## Summary

**Total Contracts**: 8
- 5 Commands (Generate, Refine, Activate, Pause, Resume, Provide Feedback)
- 2 Queries (Get Conversation, List Alerts)

**Key Features**:
- Natural language → SQL generation
- Conversational refinement with context preservation
- Confidence scoring and clarification requests
- Automatic SQL validation
- Integration with existing Subscription infrastructure
- Feedback collection for continuous improvement
- Full conversation history tracking

**Next File**: query-contracts.md (supporting read operations)

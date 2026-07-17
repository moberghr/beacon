---
title: AI Integration
description: Experimental LLM-powered features that auto-generate data source documentation and turn plain-English descriptions into SQL alerts.
---

:::caution[Experimental feature]
AI-powered features are experimental and may produce incorrect, incomplete, or misleading results. Large language models can hallucinate facts, miss important details, or generate invalid SQL. Always review and validate all AI-generated content before using in production environments.
:::

AI-powered features that leverage large language models (LLMs) to automate documentation generation and simplify alert creation.

## Purpose

AI integration in Beacon provides:
- **Automatic Documentation**: Generate comprehensive data source documentation by analyzing schemas
- **Natural Language Alerts**: Create complex SQL queries from plain English descriptions
- **Smart Insights**: AI-powered data analysis and recommendations
- **Multiple Export Formats**: Export documentation as Markdown, HTML with ERD diagrams, or PDF

## Supported LLM Providers

Beacon supports multiple LLM providers through a pluggable architecture:

| Provider | Recommended Models | Configuration |
|----------|-------------------|---------------|
| **OpenAI** | gpt-4o (recommended), gpt-4 | API key + model name |
| **Anthropic Claude** | claude-3-5-sonnet-20241022 (recommended) | API key + model name |
| **Azure OpenAI** | Latest models only | Custom base URL + deployment name |

:::note[Model recommendations]
Use the latest models only (gpt-4o, claude-3-5-sonnet). Older models like gpt-3.5-turbo and claude-3-opus are not recommended and may produce lower quality results.
:::

## Configuration

### Prerequisites

AI features require:
- LLM provider API key
- Appropriate model access
- Network connectivity to provider APIs

### Runtime Configuration via Admin Settings (Recommended)

The easiest way to configure AI is through the **Admin Settings** UI at runtime - no restart required:

1. Navigate to **Admin Settings** > **AI Configuration**
2. Select your provider (OpenAI, Anthropic, Azure OpenAI, Bedrock)
3. Enter your API key and model name
4. Configure rate limits and budget
5. Click **Save**

Changes take effect immediately. You can switch providers at runtime without restarting.

:::note
Admin Settings requires the [User Management](/features/user-management/) system to be enabled with an Admin user. See the [Admin Settings Guide](/features/admin-settings/) for details.
:::

### appsettings.json Configuration

```json
{
  "Beacon": {
    "LLM": {
      "Provider": "OpenAI",
      "ApiKey": "sk-your-api-key-here",
      "Model": "gpt-4o",
      "BaseUrl": "https://api.openai.com/v1",
      "Limits": {
        "MaxConcurrentRequests": 5,
        "RequestsPerMinute": 60,
        "MaxTokensPerRequest": 4000
      }
    }
  }
}
```

### Configuration Options

| Option | Description | Default | Required |
|--------|-------------|---------|----------|
| `Provider` | LLM provider (OpenAI, Anthropic, AzureOpenAI) | - | Yes |
| `ApiKey` | API key for authentication | - | Yes |
| `Model` | Model name or deployment ID | - | Yes |
| `BaseUrl` | API endpoint URL | Provider default | No |
| `Limits.MaxConcurrentRequests` | Max parallel requests | 5 | No |
| `Limits.RequestsPerMinute` | Rate limit per minute | 60 | No |
| `Limits.MaxTokensPerRequest` | Max tokens per request | 4000 | No |

## AI Documentation Generation

### Overview

Automatically generate comprehensive documentation by analyzing database schemas with AI.

### How It Works

1. **Schema Analysis**: Beacon fetches schema metadata (tables, columns, data types, constraints, relationships)
2. **Sample Data Collection**: Retrieves first 10 rows from each table for context
3. **AI Processing**: Sends schema structure and samples to LLM with specialized prompts
4. **Documentation Generation**: AI generates table descriptions, column explanations, relationships, and insights
5. **Storage**: Documentation is stored with versioning and edit history

### Generated Content

AI-generated documentation includes:
- **Table Descriptions**: Purpose and usage of each table
- **Column Details**: Meaning, data type explanation, and usage patterns
- **Relationships**: Foreign key relationships and join patterns
- **Data Quality Observations**: Potential issues, null patterns, constraint violations
- **Business Context**: Inferred business meaning from names and data

:::caution[Review required]
AI-generated descriptions may be inaccurate or incomplete. Always validate documentation against actual schema and business requirements before relying on it.
:::

### Export Formats

#### Markdown Export

Clean markdown format compatible with GitHub, GitLab, and documentation platforms.

```markdown
# Database Documentation

## Users Table

**Purpose**: Stores user account information

### Columns
- `id` (INT): Primary key, auto-increment
- `email` (VARCHAR): User email address, unique
- `created_at` (TIMESTAMP): Account creation timestamp
```

#### HTML Export with ERD Diagrams

Interactive HTML with:
- Collapsible sections for each table
- Table of contents with anchor navigation
- Embedded Mermaid ERD diagrams showing relationships
- Professional styling

```html
<!DOCTYPE html>
<html>
<head>
    <title>Data Source Documentation</title>
    <script src="mermaid.min.js"></script>
</head>
<body>
    <h1>Database Schema</h1>
    <div class="mermaid">
    erDiagram
        USERS ||--o{ ORDERS : places
        USERS {
            int id PK
            string email
            timestamp created_at
        }
    </div>
</body>
</html>
```

#### PDF Export

Professional PDF document with:
- Table of contents
- Schema diagrams
- Formatted tables
- Page numbers and headers

### Usage Example

**Via UI:**
1. Navigate to Data Sources
2. Select a data source
3. Click "Generate Documentation"
4. Configure options (tables to include, export format)
5. Review and export

**Via Service:**
```csharp
public class DocumentationExample
{
    private readonly IAiDocumentationService _docService;

    public async Task GenerateAsync(int dataSourceId)
    {
        var options = new GenerationOptions
        {
            Title = "Production Database Documentation",
            TableFilter = null, // All tables
            IncludeSampleData = true,
            Temperature = 0.3m, // Lower = more focused
            MaxTokens = 4000
        };

        var documentation = await _docService.GenerateDocumentationAsync(
            dataSourceId,
            userId: 1,
            options,
            CancellationToken.None);

        // Export as HTML
        var html = await _docService.ExportAsHtmlAsync(documentation.Id);

        // Export as PDF
        var pdf = await _docService.ExportAsPdfAsync(documentation.Id);
    }
}
```

## AI Alert Generation

### Overview

Create sophisticated SQL queries from natural language descriptions without writing SQL.

### How It Works

1. **Natural Language Input**: User describes alert in plain English
2. **Schema Context**: System provides data source schema to AI
3. **Query Generation**: AI generates appropriate SQL query
4. **Validation**: Generated SQL is validated for syntax correctness
5. **Review**: User reviews natural language description + generated SQL
6. **Integration**: Approved query integrates with existing Subscription system

### Example Transformations

:::caution[Validation required]
AI-generated SQL queries must be reviewed and tested before use. Queries may contain errors, inefficiencies, or may not accurately reflect the natural language intent.
:::

#### Example 1: Threshold Alert

**Natural Language:**
> "Alert me when error rates exceed 5% of total requests in the last hour"

**Generated SQL:**
```sql
SELECT
    (COUNT(CASE WHEN status_code >= 500 THEN 1 END) * 100.0 / COUNT(*)) AS error_rate
FROM requests
WHERE created_at > NOW() - INTERVAL '1 hour'
HAVING (COUNT(CASE WHEN status_code >= 500 THEN 1 END) * 100.0 / COUNT(*)) > 5;
```

#### Example 2: Percentage Change Alert

**Natural Language:**
> "Notify me when today's sales drop more than 20% compared to yesterday"

**Generated SQL:**
```sql
WITH today AS (
    SELECT SUM(amount) as total
    FROM sales
    WHERE DATE(created_at) = CURRENT_DATE
),
yesterday AS (
    SELECT SUM(amount) as total
    FROM sales
    WHERE DATE(created_at) = CURRENT_DATE - INTERVAL '1 day'
)
SELECT
    today.total as today_sales,
    yesterday.total as yesterday_sales,
    ((today.total - yesterday.total) / yesterday.total * 100) as pct_change
FROM today, yesterday
WHERE ((today.total - yesterday.total) / yesterday.total * 100) < -20;
```

#### Example 3: Orphaned Records Alert

**Natural Language:**
> "Find orders that don't have a matching customer"

**Generated SQL:**
```sql
SELECT o.id, o.customer_id, o.created_at
FROM orders o
LEFT JOIN customers c ON o.customer_id = c.id
WHERE c.id IS NULL;
```

### Usage Example

**Via Service:**
```csharp
public class AlertGenerationExample
{
    private readonly IAiAlertGenerationService _alertService;

    public async Task CreateAlertAsync(int dataSourceId)
    {
        var request = new AlertGenerationRequest
        {
            DataSourceId = dataSourceId,
            Description = "Alert when error rates exceed 5% in the last hour"
        };

        var result = await _alertService.GenerateAlertAsync(
            request,
            CancellationToken.None);

        Console.WriteLine($"Generated SQL: {result.GeneratedQuery}");
        Console.WriteLine($"Explanation: {result.Explanation}");

        // User reviews and approves...
        // Then creates subscription with generated query
    }
}
```

## Cost and Usage Tracking

### Token Usage

Each AI request consumes tokens based on:
- Schema size (number of tables/columns)
- Sample data included
- Response length

Typical token usage:
- Documentation generation (20 tables): ~3,000-5,000 tokens
- Alert generation: ~500-1,500 tokens

### Cost Estimation

Costs vary by provider and model:

| Provider | Model | Cost per 1M Tokens (Input/Output) |
|----------|-------|-----------------------------------|
| OpenAI | gpt-4o | $5.00 / $15.00 |
| OpenAI | gpt-3.5-turbo | $0.50 / $1.50 |
| Anthropic | claude-3-5-sonnet | $3.00 / $15.00 |

**Example calculation:**
- Documentation generation: 3,500 input tokens, 1,500 output tokens
- Using gpt-4o: ($5 × 3.5k/1M) + ($15 × 1.5k/1M) = $0.04 per generation

### Tracking

Usage metrics stored per request:
- `TokensUsed`: Total tokens consumed
- `EstimatedCost`: Calculated cost based on provider pricing
- `GeneratedByModel`: Model used for generation
- `GeneratedAt`: Timestamp

Query usage history:
```csharp
var documentations = await context.DataSourceDocumentations
    .Where(d => d.DataSourceId == dataSourceId)
    .Select(d => new {
        d.TokensUsed,
        d.EstimatedCost,
        d.GeneratedAt
    })
    .ToListAsync();

var totalCost = documentations.Sum(d => d.EstimatedCost);
```

## Rate Limiting

### Configuration

Rate limits prevent abuse and control costs:

```json
{
  "Beacon": {
    "LLM": {
      "Limits": {
        "MaxConcurrentRequests": 5,
        "RequestsPerMinute": 60,
        "MaxTokensPerRequest": 4000
      }
    }
  }
}
```

### Implementation

**Request Queue** (`LlmRequestQueue`):
- Enforces max concurrent requests
- Queues requests when limit reached
- Processes requests FIFO

**Provider Rate Limits**:
- OpenAI: Tier-based limits (tier 1: 500 RPM, tier 2: 5000 RPM)
- Anthropic: 50 requests/minute (free tier), higher for paid
- Azure: Configurable per deployment

### Handling Rate Limit Errors

```csharp
try
{
    var result = await _llmProvider.CompleteAsync(request, cancellationToken);
}
catch (RateLimitException ex)
{
    // Wait and retry
    await Task.Delay(ex.RetryAfterSeconds * 1000);
    // Retry logic...
}
```

## Security Considerations

### API Key Security

:::caution
Never commit API keys to source control.
:::

**Best practices:**
1. Store API keys in environment variables
2. Use secrets management (Azure Key Vault, AWS Secrets Manager)
3. Rotate keys regularly
4. Use different keys for dev/staging/production

```json
{
  "Beacon": {
    "LLM": {
      "ApiKey": "${LLM_API_KEY}"  // Environment variable
    }
  }
}
```

### Data Privacy

AI providers receive:
- Database schema structure (table/column names)
- Sample data (first 10 rows)
- Natural language descriptions

**Recommendations:**
1. Exclude sensitive tables from AI analysis
2. Filter sensitive columns before sending to AI
3. Use de-identified sample data when possible
4. Review provider data retention policies
5. Consider self-hosted LLM for sensitive data

### Configuration Example with Filtering

```csharp
var options = new GenerationOptions
{
    TableFilter = t => !t.Name.Contains("sensitive"),
    ExcludeColumns = new[] { "password", "ssn", "credit_card" }
};
```

## Troubleshooting

### Common Issues

**Issue:** "LLM configuration not found"
**Solution:** Ensure `Beacon:LLM` section exists in appsettings.json

**Issue:** "Invalid API key"
**Solution:** Verify API key is correct and has appropriate permissions

**Issue:** Rate limit exceeded
**Solution:** Reduce `RequestsPerMinute` or increase provider tier

**Issue:** Generated SQL is invalid
**Solution:** AI is probabilistic and experimental - always review and test queries before use. Manual adjustment may be required.

### Debugging

Enable verbose logging:
```json
{
  "Logging": {
    "LogLevel": {
      "Beacon.Core.Services.Ai": "Debug",
      "Beacon.Core.Services.LlmProviders": "Debug"
    }
  }
}
```

## Architecture

### Service Layer

```
IAiDocumentationService
├── GenerateDocumentationAsync()
├── ExportAsMarkdownAsync()
├── ExportAsHtmlAsync()
└── ExportAsPdfAsync()

IAiAlertGenerationService
├── GenerateAlertAsync()
└── RefineAlertAsync()

ILlmProvider (interface)
├── DelegatingLlmProvider (proxy - injected everywhere)
│   └── Delegates to LlmProviderManager.CurrentProvider
├── OpenAiProvider
├── AnthropicProvider
├── AzureOpenAiProvider
└── BedrockProvider

LlmProviderFactory
└── CreateProvider() -> ILlmProvider

LlmProviderManager (implements ILlmConfigurationUpdater)
├── CurrentProvider (hot-swappable)
└── UpdateConfiguration() (called by Admin Settings)
```

### Hot-Swap Flow

All consumers inject `ILlmProvider`, which is registered as `DelegatingLlmProvider`. This proxy delegates to the current provider held by `LlmProviderManager`. When Admin Settings saves a new LLM configuration, `LlmProviderManager` recreates the provider and all subsequent requests use the new provider automatically.

### Data Model

```
DataSourceDocumentation
├── Id
├── DataSourceId
├── Title
├── GeneratedByModel
├── GeneratedAt
├── TokensUsed
├── EstimatedCost
└── Sections (List<DocumentationSection>)

DocumentationSection
├── Id
├── DocumentationId
├── SectionType (Table, Column, Relationship, etc.)
├── Content (markdown)
└── IsAiGenerated

AiAlertConfiguration
├── Id
├── DataSourceId
├── NaturalLanguageDescription
├── GeneratedQuery
├── Explanation
└── CreatedAt
```

## Related Features

- [Admin Settings](/features/admin-settings/) - Configure and hot-swap LLM providers at runtime
- [Data Sources](/features/data-sources/) - Configure data sources for AI analysis
- [Subscriptions](/features/subscriptions/) - Use AI-generated queries in subscriptions
- [Anomaly Detection](/features/anomaly-detection/) - Statistical anomaly detection complement to AI

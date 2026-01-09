# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands
- Build solution: `dotnet build --property WarningLevel=0`
- Run application: `dotnet run --project Semantico.SampleProject`
- Watch for changes: `dotnet watch run --project Semantico.SampleProject`

## Code Style Guidelines
- **Naming**: PascalCase for classes, methods, properties; camelCase for parameters, local variables
- **Organization**: Group related files in folders based on domain/functionality
- **Architecture**: Follow Clean Architecture principles with Core containing domain logic
- **Error Handling**: Use exceptions with custom SemanticoException class for domain errors
- **Imports**: Organize by System namespaces first, then third-party, then project namespaces
- **Types**: Prefer strong typing with explicit models for data transfer

### Entity Design
- Implement `IChangeableEntity` for modifiable entities
- Inherit from `BaseArchivableEntity` for archivable entities
- Use `null!` for required string properties
- Place nullable types after non-nullable properties
- Add appropriate indexes in context's OnModelCreating method for properties that will be queried frequently

### Database Operations

**IMPORTANT: Database migrations will be created by the user manually. When entity changes are made, Claude should only mention that a migration is needed - DO NOT attempt to create or run migrations.**

```bash
# Generate schema-agnostic migration (ensure Program.cs uses default "semantico" schema)
dotnet ef migrations add MigrationName --project Semantico.Core --startup-project Semantico.SampleProject

# For PostgreSQL provider specifically
dotnet ef migrations add MigrationName --project Semantico.Core.PostgreSql --startup-project Semantico.SampleProject

# For SQL Server provider specifically
dotnet ef migrations add MigrationName --project Semantico.Core.SqlServer --startup-project Semantico.SampleProject

# Update database (uses schema specified in Program.cs)
dotnet ef database update --project Semantico.Core --startup-project Semantico.SampleProject

# IMPORTANT: Migrations are schema-agnostic. The schema is specified at runtime via:
# services.AddPostgreSqlSemantico(connectionString, "your_schema_name")
```

### Handler Structure
- Create `internal sealed class` implementing `IRequestHandler<TRequest, TResponse>`
- Define request/response as records at file end (not with "// Request/Response at end of file" comment)
- Use primary constructor injection for dependencies

## Project Structure
- Semantico.Core: Core domain model, services, data access
- Semantico.UI: Blazor UI components
- Semantico.SampleProject: Sample implementation/application

## Active Technologies
- C# 13 / .NET 9.0 + EF Core 9.0, MediatR, Blazor Server
- PostgreSQL (primary) and SQL Server (secondary) via provider-specific projects
- Scheduled jobs: Core provides interfaces (e.g., `IJobScheduler`); consumers implement with their preferred scheduler (Hangfire used in SampleProject as example)
- **AI/LLM Integration**: OpenAI, Anthropic Claude, Azure OpenAI support for documentation generation and alert creation
- **Anomaly Detection**: Statistical anomaly detection with baseline learning (Z-score, IQR, percentage change methods)
- **Document Generation**: QuestPDF (PDF), Markdig (Markdown), Mermaid.js (ERD diagrams)

## Recent Changes (005-ai-integration - January 2026)
- **EncryptionKey**: Now **required** configuration for AES-256 encryption of connection strings (see Configuration section below)
- **AI Documentation Service**: Automatic data source documentation generation with LLM providers
- **AI Alert Generation Service**: Natural language to SQL query conversion for alert creation
- **Anomaly Detection Service**: Statistical anomaly detection with configurable thresholds and baseline learning
- **LLM Provider Factory**: Pluggable LLM provider architecture (OpenAI, Claude, Azure OpenAI)
- **Document Export**: PDF, HTML, Markdown generation with QuestPDF and Markdig
- **Slack Notifications**: Added Slack notification channel with superior table formatting (December 2025)

## Notification System Architecture

### Overview
Semantico supports multiple notification channels for query result delivery:
- **Email**: HTML table + CSV attachment (full results)
- **Microsoft Teams**: Adaptive Cards
- **Slack**: Block Kit with Table Blocks (NEW - December 2025)
- **Jira**: Issue creation and updates

### Adapter Pattern
All notification channels implement the `IAdapter` interface:
```csharp
public interface IAdapter
{
    NotificationType NotificationType { get; }
    Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int? lastNotificationResultCount);
}
```

Adapters are registered in DI and routed via `AdapterFactory` based on `NotificationType` enum.

## Slack Notification Integration (December 2025)

### Implementation Details

**Key Files:**
- `Semantico.Core/Adapters/Slack/SlackAdapter.cs` - Main implementation (310 lines)
- `Semantico.Core/Data/Enums/NotificationType.cs` - Added `Slack = 4`
- `Semantico.Core/ServiceConfiguration.cs` - DI registration
- `Semantico.UI/Components/Pages/Recipients/AddRecipientDialog.razor` - UI for adding Slack recipients
- `Semantico.UI/Components/Pages/Recipients/UpdateRecipientDialog.razor` - UI for updating Slack recipients

### Slack Advantages Over Teams

| Feature | Microsoft Teams | Slack |
|---------|----------------|-------|
| **Max Columns** | 3 (self-imposed limit) | 5 (optimal for readability) |
| **Max Rows** | 10 (truncated) | 25 (balanced display) |
| **Display Format** | Adaptive Cards table | ASCII table in code block |
| **Column Alignment** | Limited | Auto-sized, centered headers |
| **Text Formatting** | Plain text only | Monospace with padding |
| **Implementation** | JSON Adaptive Card | Fixed-width text formatting |

**Note:** Slack's Table Block API is only available via `chat.postMessage` (OAuth), not incoming webhooks. Semantico uses ASCII-style tables in code blocks for webhook compatibility - this provides proper table layout with column alignment.

### SlackAdapter Architecture

**Message Structure:**
1. **Blocks** (main message content):
   - Header block: Query name with plain_text formatting
   - Section block: SQL query in code block (if ShowQuery enabled)
   - Divider block: Visual separator
   - Section block: Summary ("Showing X of Y total records")
   - Actions block: "View Full Results" button (if BaseUrl configured)
   - **Data blocks**: Section blocks with fields for each record (see below)

2. **Data Display** (using Code Block for table layout - webhook compatible):
   - Results displayed as ASCII-style table in code block
   - Monospace font ensures proper column alignment
   - Columns auto-sized based on content (max 30 chars per column)
   - Clean separator lines between header and data rows
   - Fixed-width formatting preserves table structure

**JSON Payload Structure:**
```json
{
  "text": "[Semantico] DataSource - Subscription",
  "blocks": [
    {"type": "header", "text": {"type": "plain_text", "text": "..."}},
    {"type": "section", "text": {"type": "mrkdwn", "text": "*Query:*\n```sql\n...\n```"}},
    {"type": "divider"},
    {"type": "section", "text": {"type": "mrkdwn", "text": "*Results:* ..."}},
    {"type": "actions", "elements": [{"type": "button", "url": "..."}]},
    {
      "type": "section",
      "text": {
        "type": "mrkdwn",
        "text": "```\nColumn1    | Column2    | Column3\n-----------+------------+-----------\nValue1     | Value2     | Value3\nValue4     | Value5     | Value6\n```"
      }
    }
  ]
}
```

**Why Not Table Blocks?**
Slack's Table Block API (`type: "table"`) is only available via `chat.postMessage` with OAuth tokens. Incoming webhooks do not support table blocks - attempting to use them results in `invalid_blocks` or `invalid_attachments` errors.

**Workaround:** ASCII-style tables in code blocks provide the best alternative for webhook-based notifications. The monospace font ensures proper alignment and the fixed-width format preserves table structure across all Slack clients.

### Date/Value Formatting

**Automatic formatting in `FormatCellValue()`:**
- `DateTime`: "yyyy-MM-dd HH:mm:ss"
- `DateTimeOffset`: "yyyy-MM-dd HH:mm:ss"
- `DateOnly`: "yyyy-MM-dd"
- `TimeOnly`: "HH:mm:ss"
- `bool`: "Yes" / "No"
- `null`: empty string

### Slack API Limits

**Hard Limits (enforced by Slack):**
- Maximum 50 blocks per message
- Maximum 10 fields per section block
- Maximum 3000 characters per field text

**Implementation Limits:**
- `MaxColumns = 5` - optimal for readability in Slack code blocks
- `MaxRows = 25` - balanced between detail and readability
- Column width: minimum 3, maximum 20 characters (truncated with ellipsis)
- Headers are center-aligned, data is left-aligned with padding
- Empty cells display as spaces (proper alignment maintained)
- Entire table uses single code block (highly efficient)

### Configuration

**Webhook Setup:**
1. Create Slack app at https://api.slack.com/apps
2. Enable "Incoming Webhooks"
3. Add webhook to workspace
4. Copy webhook URL (format: `https://hooks.slack.com/services/T00000000/B00000000/XXXX...`)
5. Store in Recipient.Destination field

**BaseUrl for Links:**
Configure in `SemanticoConfiguration`:
```csharp
builder.Services.AddSemanticoAdmin(builder.Configuration, options =>
{
    options.BaseUrl = "https://yourdomain.com/semantico";
});
```

This enables the "View Full Results" button linking to:
`{BaseUrl}/notifications/{notificationId}`

### Testing

**Manual webhook test:**
```bash
curl -X POST 'https://hooks.slack.com/services/...' \
  -H 'Content-Type: application/json' \
  -d '{
    "text": "Test from Semantico",
    "blocks": [{"type": "section", "text": {"type": "mrkdwn", "text": "Test"}}]
  }'
```

### Database Schema

**No migration required!**
- NotificationType enum stored as integer in database
- Existing Recipient.Destination column supports webhook URLs
- Backward compatible with existing notifications

### UI Updates

**Recipient Forms:**
- Added "Slack" option to NotificationType dropdown
- Updated destination label: "Slack Webhook URL"
- Updated helper text: "Enter the Slack incoming webhook URL"

**Files Modified:**
- `AddRecipientDialog.razor` - Line 17: Added Slack option
- `UpdateRecipientDialog.razor` - Line 44: Added Slack option

### Documentation Updates

**Files Updated:**
- `Semantico.UI/Components/Pages/About.razor`:
  - Line 53: Mentioned Slack in Smart Alerting section
  - Line 121: Added SlackAdapter to architecture diagram
  - Line 333: Added Slack to Multi-Channel Notifications list

- `README.md`:
  - Line 20: Added Slack to Smart Alerting
  - Line 83: Added SlackAdapter to architecture diagram
  - Line 209: Added Slack to Multi-Channel Notifications

- `docs/features/notifications.md`:
  - Line 29: Added Slack to channel comparison table
  - Lines 243-402: Full Slack documentation section
  - Line 471: Updated table size limits
  - Lines 512, 520: Added Slack to delivery status sections

### Future Enhancements

**Potential improvements:**
1. **Rich text cells**: Add clickable links within cells (e.g., link to user profile)
2. **Emoji indicators**: Add visual indicators for alerts (🔴 critical, 🟡 warning)
3. **Mentions**: Add @user mentions for specific columns (requires user ID mapping)
4. **Thread replies**: Post follow-up results as threaded replies (requires storing thread_ts)
5. **Interactive elements**: Add buttons for actions (e.g., "Acknowledge", "Resolve")

**Note**: These enhancements require additional configuration and may need UI changes.

## Encryption Configuration (Required - January 2026)

### EncryptionKey Requirement

**IMPORTANT**: The `Semantico:EncryptionKey` configuration is now **required** (as of 005-ai-integration branch).

**Purpose**: Used for AES-256 encryption of sensitive data, primarily connection strings stored in the `DataSource` entity.

**Configuration:**
```json
{
  "Semantico": {
    "EncryptionKey": "your-secure-32-character-key-here"
  }
}
```

**Generate a secure key:**
```bash
openssl rand -base64 32
```

**Implementation:**
- `Semantico.Core/Services/EncryptionService.cs` - AES-256 encryption/decryption
- `Semantico.Core/ServiceConfiguration.cs` - Validates key is present on startup
- Connection strings are encrypted when saved and decrypted when retrieved

**Error if missing:**
```
InvalidOperationException: "Semantico:EncryptionKey must be configured.
Generate a secure key with: openssl rand -base64 32
Then add to appsettings.json: { "Semantico": { "EncryptionKey": "your-generated-key" } }"
```

**Key Files:**
- `Semantico.Core/ServiceConfiguration.cs` (lines 41-50) - Validation and registration
- `Semantico.Core/Services/EncryptionService.cs` - Implementation
- `Semantico.SampleProject/appsettings.json` - Configuration example

## AI Integration Architecture (January 2026) - EXPERIMENTAL

⚠️ **IMPORTANT: AI features are experimental and may produce incorrect or incomplete results. Always review and validate AI-generated content before use in production.**

### Overview

Semantico integrates LLM providers for two primary use cases:
1. **Automatic Documentation Generation**: Analyze data source schemas and generate comprehensive documentation
2. **Natural Language Alert Creation**: Convert plain English descriptions to SQL queries

### LLM Configuration

**appsettings.json:**
```json
{
  "Semantico": {
    "LLM": {
      "Provider": "OpenAI",
      "ApiKey": "your-api-key-here",
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

**Supported Providers:**
- **OpenAI**: `gpt-4o` (recommended), `gpt-4`
- **Anthropic Claude**: `claude-3-5-sonnet-20241022` (recommended)
- **Azure OpenAI**: Configure with custom `BaseUrl` and deployment name

⚠️ **Note:** Older models (gpt-3.5-turbo, claude-3-opus) are not recommended for production use.

### Key Services

**AI Documentation Service** (`Semantico.Core/Services/Ai/AiDocumentationService.cs`):
- Fetches schema metadata via `IDatabaseMetadataService`
- Constructs prompts with schema structure and sample data
- Calls LLM provider to generate documentation
- Stores results in `DataSourceDocumentation` entity
- Exports to Markdown, HTML (with Mermaid ERD diagrams), or PDF

**AI Alert Generation Service** (`Semantico.Core/Services/Ai/AiAlertGenerationService.cs`):
- Accepts natural language alert descriptions
- Constructs prompt with data source schema context
- Generates SQL queries from natural language
- Validates generated SQL before returning
- Stores in `AiAlertConfiguration` entity

**LLM Provider Factory** (`Semantico.Core/Services/LlmProviders/LlmProviderFactory.cs`):
- Creates appropriate provider based on configuration
- Supports OpenAI, Claude, Azure OpenAI
- Handles rate limiting via `LlmRequestQueue`
- Tracks token usage and costs

### Document Export

**PDF Generation** (QuestPDF):
- Full documentation with table of contents
- Schema diagrams and table structures
- Professional formatting

**HTML Export** (Markdig + Mermaid.js):
- Interactive HTML with collapsible sections
- Embedded Mermaid ERD diagrams
- Table of contents with anchor links

**Markdown Export**:
- Clean markdown format
- Compatible with GitHub, GitLab, etc.

### Key Entities

**DataSourceDocumentation**:
- Stores AI-generated documentation
- Tracks model used, tokens consumed, cost
- Version history support

**DocumentationSection**:
- Individual sections (table descriptions, column details)
- Distinguishes AI-generated vs user-edited content

**DocumentationExport**:
- Cached HTML exports
- Regenerates only when documentation changes

**AiAlertConfiguration**:
- Natural language description
- Generated SQL query
- AI reasoning/explanation

## Anomaly Detection System (January 2026)

### Overview

Statistical anomaly detection for subscriptions that learns from historical data and alerts on deviations.

### Detection Methods

**1. Standard Deviation (Z-Score)**:
- Calculates mean and standard deviation from historical baselines
- Flags values outside N standard deviations (configurable, default 2-3)
- Best for: Normally distributed data

**2. Interquartile Range (IQR)**:
- Calculates Q1, Q3, and IQR from historical data
- Flags values outside Q1 - (1.5 * IQR) or Q3 + (1.5 * IQR)
- Best for: Skewed distributions, outlier detection

**3. Percentage Change**:
- Compares current value to most recent historical value
- Flags if percentage change exceeds threshold (e.g., >50% increase)
- Best for: Trend monitoring, business metrics

### Configuration

**Entity:** `AnomalyConfig` (per subscription)
```csharp
public class AnomalyConfig
{
    public int SubscriptionId { get; set; }
    public bool Enabled { get; set; }
    public AnomalyDetectionMethod DetectionMethod { get; set; } // StandardDeviation, IQR, PercentageChange
    public int LookbackDays { get; set; } // How far back to analyze
    public int MinimumDataPoints { get; set; } // Min history before detecting
    public decimal Threshold { get; set; } // Sensitivity (e.g., 2.5 std devs)
}
```

### Baseline Learning

**Entity:** `AnomalyBaseline`
```csharp
public class AnomalyBaseline
{
    public int SubscriptionId { get; set; }
    public DateTime ExecutionTime { get; set; }
    public decimal MetricValue { get; set; } // Row count from query result
}
```

**Process:**
1. Every subscription execution stores row count in `AnomalyBaseline`
2. System accumulates historical data over time
3. Once `MinimumDataPoints` is reached, anomaly detection activates
4. Baseline continuously refines as more data is collected

### Usage

**Service:** `IAnomalyDetectionService`
```csharp
public interface IAnomalyDetectionService
{
    Task<AnomalyEvaluationResult> EvaluateAnomalyAsync(
        int subscriptionId,
        int rowCount,
        CancellationToken cancellationToken = default);

    Task StoreBaselineAsync(
        int subscriptionId,
        decimal metricValue,
        DateTime executionTime,
        CancellationToken cancellationToken = default);
}
```

**Integration:**
- Called automatically during subscription execution
- If anomaly detected, creates alert task or sends notification
- Result includes: IsAnomaly flag, explanation, historical metrics

### Example Scenario

**Subscription**: Monitor failed login attempts
**Query**: `SELECT COUNT(*) FROM audit_log WHERE event = 'login_failed' AND created_at > NOW() - INTERVAL '1 hour'`

**Normal baseline**: 5-15 failed logins per hour (learned from 30 days of history)
**Anomaly detected**: 150 failed logins in current execution
**Action**: Create high-priority task or send immediate notification

### Key Files

- `Semantico.Core/Services/AnomalyDetectionService.cs` - Main implementation
- `Semantico.Core/Data/Entities/AnomalyConfig.cs` - Configuration entity
- `Semantico.Core/Data/Entities/AnomalyBaseline.cs` - Historical data storage
- `Semantico.Core/Data/Enums/AnomalyDetectionMethod.cs` - Detection method enum
- `Semantico.Core/Models/Anomaly/AnomalyEvaluationResult.cs` - Result model

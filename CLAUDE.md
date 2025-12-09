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
- C# 12 / .NET 8.0 + EF Core 8.0, MediatR, Blazor Server (004-alerting-tasks)
- PostgreSQL (primary) and SQL Server (secondary) via provider-specific projects (004-alerting-tasks)
- Scheduled jobs: Core provides interfaces (e.g., `IJobScheduler`); consumers implement with their preferred scheduler (Hangfire used in SampleProject as example)

## Recent Changes
- 004-alerting-tasks: Added C# 12 / .NET 8.0 + EF Core 8.0, MediatR, Blazor Server
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

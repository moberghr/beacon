# Beacon Notification Adapters

## Overview

Notification adapters implement the `IAdapter` interface to deliver query results to various channels. The system uses a factory pattern for routing based on `NotificationType`.

---

## Architecture

### IAdapter Interface

**File:** `src/Beacon.Core/Adapters/IAdapter.cs`

```csharp
internal interface IAdapter
{
    public NotificationType NotificationType { get; }

    /// <summary>
    /// If lastNotificationResultCount is not null, it indicates a follow-up notification.
    /// For Jira: won't create new issue, will update existing one.
    /// </summary>
    public Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int? lastNotificationResultCount);
}
```

### AdapterFactory

**File:** `src/Beacon.Core/Adapters/AdapterFactory.cs`

```csharp
internal class AdapterFactory
{
    private readonly IEnumerable<IAdapter> _adapters;

    public AdapterFactory(IEnumerable<IAdapter> adapters)
    {
        _adapters = adapters;
    }

    public IAdapter GetAdapterService(NotificationType notificationType)
    {
        return _adapters.FirstOrDefault(e => e.NotificationType == notificationType)
               ?? throw new NotSupportedException();
    }
}
```

### Service Registration

**File:** `src/Beacon.Core/ServiceConfiguration.cs`

```csharp
// Register all adapters as singletons
services.AddSingleton<IAdapter, TeamsAdapter>();
services.AddSingleton<IAdapter, SlackAdapter>();
services.AddSingleton<IAdapter, EmailAdapter>();
services.AddSingleton<IAdapter, JiraAdapter>();

// Register factory
services.AddSingleton<AdapterFactory>();
```

---

## RecipientQueryResult

**File:** `src/Beacon.Core/Adapters/RecipientQueryResult.cs`

DTO passed to all adapters containing:

```csharp
public class RecipientQueryResult
{
    public QueryResult QueryResult { get; set; }
    public string RecipientDestination { get; set; }  // Webhook URL, email, etc.
    public NotificationType RecipientNotificationType { get; set; }
    public int? NotificationId { get; set; }  // For "View Results" links
}
```

---

## Slack Adapter

**File:** `src/Beacon.Core/Adapters/Slack/SlackAdapter.cs`
**NotificationType:** `Slack = 4`

### Features
- Block Kit API with Table Blocks
- Up to 20 columns, 100 rows
- Smart column alignment by data type
- Text wrapping for text columns
- Markdown SQL code blocks
- Action button for viewing full results

### Destination Format
```
https://hooks.slack.com/services/T00000000/B00000000/XXXXXXXXXXXXXXXXXXXXXXXX
```

### Message Structure

```json
{
  "text": "[Beacon] DataSource - SubscriptionName",
  "blocks": [
    { "type": "header", "text": { "type": "plain_text", "text": "..." } },
    { "type": "section", "text": { "type": "mrkdwn", "text": "**Query:**\n```sql\n...\n```" } },
    { "type": "divider" },
    { "type": "section", "text": { "type": "mrkdwn", "text": "*Results:* Showing X of Y" } },
    { "type": "actions", "elements": [{ "type": "button", "url": "..." }] }
  ],
  "attachments": [{
    "blocks": [{
      "type": "table",
      "rows": [...],
      "column_settings": [{ "align": "left|center|right", "is_wrapped": true|false }]
    }]
  }]
}
```

### Column Type Detection

```csharp
private enum ColumnType { Text, Number, Date, Boolean }

// Alignment rules:
// Number → right
// Date/DateTime → center
// Boolean → center
// Text → left

// Wrapping: enabled only for Text columns
```

### Limits
- Max columns: 20
- Max rows: 100
- Rich text in headers (bold)
- Automatic date/time formatting

### Key Implementation

```csharp
internal class SlackAdapter(IHttpClientFactory httpClientFactory, BeaconConfiguration configuration) : IAdapter
{
    private const int MaxColumns = 20;
    private const int MaxRows = 100;

    public NotificationType NotificationType => NotificationType.Slack;

    public async Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int? lastNotificationResultCount)
    {
        var client = httpClientFactory.CreateClient();
        var message = BuildSlackMessage(queryResult, notificationId);

        var jsonPayload = JsonSerializer.Serialize(message, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        await client.PostAsync(recipientQueryResult.RecipientDestination, content);
    }
}
```

---

## Teams Adapter

**File:** `src/Beacon.Core/Adapters/Teams/TeamsAdapter.cs`
**NotificationType:** `Teams = 1`

### Features
- Microsoft Adaptive Cards 1.5
- Table display (3 columns max, 10 rows)
- Grid styling with emphasis
- Action button for viewing full results

### Destination Format
```
https://outlook.office.com/webhook/...
```

### Card Structure

```csharp
var card = new AdaptiveCard("1.5")
{
    Type = "AdaptiveCard",
    Body = new List<AdaptiveElement>
    {
        new AdaptiveTextBlock { Text = "Title", Size = Large, Weight = Bolder },
        new AdaptiveTextBlock { Text = "Query = ...", Wrap = true },
        new AdaptiveTextBlock { Text = "First 10 records", Weight = Bolder },
        new AdaptiveTable { Columns, Rows, FirstRowAsHeaders = true, ShowGridLines = true }
    },
    Actions = new List<AdaptiveAction>
    {
        new AdaptiveOpenUrlAction { Title = "View Query Results", Url = "..." }
    }
};
```

### Limits
- Max columns: 3
- Max rows: 10 (preview)
- Uses MessageCardModel NuGet for Adaptive Cards

### Key Implementation

```csharp
internal class TeamsAdapter(IHttpClientFactory httpClientFactory, BeaconConfiguration configuration) : IAdapter
{
    public NotificationType NotificationType => NotificationType.Teams;

    public async Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int? lastNotificationResultCount)
    {
        var card = new AdaptiveCard("1.5") { ... };
        var jsonPayload = card.ToJson();
        await client.PostAsync(recipientQueryResult.RecipientDestination, content);
    }

    private AdaptiveTable GenerateAdaptiveTableFromQueryResults(List<IDictionary<string, object?>> queryResults)
    {
        var columnNames = queryResults.First().Keys.Take(3).ToList();  // 3 columns max
        // Build header row and data rows
    }
}
```

---

## Email Adapter

**File:** `src/Beacon.Core/Adapters/Email/EmailAdapter.cs`
**NotificationType:** `Email = 2`

### Features
- HTML email with styled table
- CSV or XLSX attachment (configurable)
- All rows in attachment
- SMTP via configured provider

### Destination Format
```
user@domain.com
```

### Email Structure
- Subject: `[Beacon] {DataSourceName} - {SubscriptionName}`
- Body: HTML table with first 10 rows
- Attachment: Full results as CSV or XLSX

### Attachment Generation

Uses ClosedXML for Excel and CsvHelper for CSV:

```csharp
// Excel attachment
using var workbook = new XLWorkbook();
var worksheet = workbook.Worksheets.Add("Results");
// Add headers and data rows

// CSV attachment
using var writer = new StreamWriter(stream);
using var csv = new CsvWriter(writer, config);
// Write headers and records
```

### Configuration

```csharp
public class EmailConfiguration
{
    public string SmtpHost { get; set; }
    public int SmtpPort { get; set; }
    public string Username { get; set; }
    public string Password { get; set; }
    public string FromAddress { get; set; }
    public string FromName { get; set; }
}
```

---

## Jira Adapter

**File:** `src/Beacon.Core/Adapters/Jira/JiraAdapter.cs`
**NotificationType:** `Jira = 3`

### Features
- Creates Jira issues on first notification
- Updates existing issues on follow-up notifications
- Adds comments with result summary
- Full result attachment

### Destination Format
```
domain;project;email;apikey
```

Example: `company.atlassian.net;PROJ;user@company.com;ATATT3xFfGF0...`

### Behavior

| Scenario | Action |
|----------|--------|
| First notification | Create new issue with description and attachment |
| Follow-up (existing issue) | Add comment to existing issue |
| Result count = 0 | Can auto-close issue (configurable) |

### Issue Structure

```markdown
**Summary:** [Beacon] {DataSourceName} - {SubscriptionName}

**Description:**
Query: {SqlQuery}

Results: {ResultCount} rows found

| Col1 | Col2 | Col3 |
|------|------|------|
| ...  | ...  | ...  |

**Attachment:** results.csv or results.xlsx
```

### API Integration

Uses Atlassian.SDK:

```csharp
var jira = Jira.CreateRestClient(domain, email, apiKey);

// Create issue
var issue = jira.CreateIssue(projectKey);
issue.Type = "Task";
issue.Summary = summary;
issue.Description = description;
await issue.SaveChangesAsync();

// Add comment
await issue.AddCommentAsync(comment);

// Add attachment
issue.AddAttachment(attachmentPath);
```

---

## Adapter Comparison

| Feature | Slack | Teams | Email | Jira |
|---------|-------|-------|-------|------|
| Max Columns | 20 | 3 | Unlimited | Unlimited |
| Max Rows (inline) | 100 | 10 | 10 | 10 |
| Full Results | Via link | Via link | Attachment | Attachment |
| SQL Display | Code block | Text | HTML | Markdown |
| Smart Alignment | Yes | No | No | No |
| Issue Tracking | No | No | No | Yes |
| Rich Formatting | Block Kit | Adaptive Cards | HTML | Markdown |

---

## Adding a New Adapter

1. **Create adapter class:**

```csharp
namespace Beacon.Core.Adapters.NewChannel;

internal class NewChannelAdapter(IHttpClientFactory httpClientFactory, BeaconConfiguration configuration) : IAdapter
{
    public NotificationType NotificationType => NotificationType.NewChannel;

    public async Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int? lastNotificationResultCount)
    {
        // Implementation
    }
}
```

2. **Add to NotificationType enum:**

```csharp
// src/Beacon.Core/Data/Enums/NotificationType.cs
public enum NotificationType
{
    Teams = 1,
    Email = 2,
    Jira = 3,
    Slack = 4,
    NewChannel = 5  // Add new value
}
```

3. **Register in ServiceConfiguration:**

```csharp
services.AddSingleton<IAdapter, NewChannelAdapter>();
```

4. **Add UI support in AddRecipientDialog.razor:**

```razor
<MudSelectItem Value="NotificationType.NewChannel">NewChannel</MudSelectItem>
```

5. **Update destination helpers:**

```csharp
private string GetDestinationLabel() => Recipient.NotificationType switch
{
    NotificationType.NewChannel => "NewChannel Webhook URL",
    // ...
};
```

---

## Configuration

### BeaconConfiguration

**File:** `src/Beacon.Core/BeaconConfiguration.cs`

```csharp
public class BeaconConfiguration
{
    /// <summary>
    /// Base URL for "View Results" links in notifications
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Email configuration
    /// </summary>
    public EmailConfiguration? Email { get; set; }
}
```

### Usage in appsettings.json

```json
{
  "Beacon": {
    "BaseUrl": "https://your-domain.com/beacon",
    "Email": {
      "SmtpHost": "smtp.example.com",
      "SmtpPort": 587,
      "Username": "user",
      "Password": "password",
      "FromAddress": "beacon@example.com",
      "FromName": "Beacon Alerts"
    }
  }
}
```

---

## Common Dependencies

All adapters inject:
- `IHttpClientFactory` - HTTP client management
- `BeaconConfiguration` - BaseUrl for links, email settings

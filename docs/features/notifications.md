---
layout: default
title: Notifications
parent: Features
nav_order: 5
---

# Notifications

Notifications deliver query results to recipients via Email, Microsoft Teams, or Jira.

## Purpose

Notifications allow you to:
- Deliver query results automatically
- Support multiple delivery channels (Email, Teams, Jira)
- Format results as tables or CSV attachments
- Track delivery status and history
- Handle delivery failures gracefully

## Notification Channels

Beacon supports three notification channels:

| Channel | Format | Use Case |
|---------|--------|----------|
| **Email** | HTML table + **CSV attachment with full results** | Scheduled reports, data exports, stakeholder delivery |
| **Microsoft Teams** | Adaptive card with formatted table | Real-time alerts, team collaboration |
| **Slack** | Rich Table Blocks with 20 columns, 100 rows | Real-time alerts, team collaboration, superior table formatting |
| **Jira** | Issue creation with results | Incident tracking, workflow integration |

{: .note }
> **Reporting Feature**: Email is the only channel that delivers complete query results as attachments. This makes it perfect for scheduled reporting where stakeholders need full datasets for analysis in Excel or other tools.

## Email Notifications

### Prerequisites

Email notifications require implementing `IEmailAdapter` in your application:

```csharp
using Beacon.Core.Adapters.Mail;

public class SmtpEmailAdapter : IEmailAdapter
{
    public async Task SendEmailAsync(string to, string subject, string body, QueryResultFile? queryResultAttachmentFile)
    {
        // Implement using your email provider
        // (SMTP, SendGrid, AWS SES, etc.)
    }
}
```

Register in your Program.cs:

```csharp
builder.Services.AddBeacon(builder.Configuration, options =>
{
    options.UsePostgreSql(builder.Configuration.GetConnectionString("BeaconContext")!, "beacon");
    options.AddBeaconScheduler<YourScheduler>();
    options.AddEmailAdapter<SmtpEmailAdapter>();
});
```

📚 [See complete email adapter implementation →](../getting-started/configuration#email-adapter)

### Email Format

Emails include:
- **Subject**: Query name and execution timestamp
- **Body**:
  - Query description
  - Execution summary (rows returned, execution time)
  - Results formatted as HTML table (first 20 rows)
  - Link to execution history
- **Attachment**: **CSV file with FULL results** (regardless of row count)

{: .note }
> **Powerful Reporting Feature**: Email attachments contain the complete query result set, making Beacon perfect for scheduled reports. Recipients can open the CSV directly in Excel for analysis.

**Example email:**

```
Subject: Daily User Count Report - 2025-10-22 09:00:00

Beacon Query Execution

Query: Daily Active Users
Description: Count of users active in last 24 hours
Executed: 2025-10-22 09:00:15
Execution Time: 0.34 seconds
Rows Returned: 1

Results:
+---------------+
| active_users  |
+---------------+
| 1,247         |
+---------------+

View full execution history:
https://your-beacon-instance/notifications/12345
(root-relative React route: /notifications/12345)

---
Sent by Beacon
```

### Creating Email Recipient

1. Navigate to **Recipients** → **Create New Recipient**
2. Fill in details:
   - **Name**: `DevOps Team`
   - **Type**: Email
   - **Email Address**: `devops@yourdomain.com`
3. Click **Save**

### Adding to Subscription

1. Open your subscription
2. In **Recipients** section, click **Add Recipient**
3. Select your email recipient
4. Click **Save**

## Microsoft Teams Notifications

### Prerequisites

Teams notifications require a webhook URL from your Teams channel.

### Getting Teams Webhook URL

1. Open your Teams channel
2. Click **...** (More options) next to channel name
3. Select **Connectors**
4. Search for **Incoming Webhook**
5. Click **Configure**
6. Enter webhook details:
   - **Name**: `Beacon Alerts`
   - **Upload Image**: (optional, Beacon logo)
7. Click **Create**
8. **Copy the webhook URL** (you'll need this)
9. Click **Done**

### Teams Card Format

Teams notifications use Adaptive Cards with:
- **Header**: Query name and icon
- **Summary**: Execution details
- **Results Table**: Formatted data (up to 20 rows)
- **Actions**: "View Query Results" button linking to notification details (when BaseUrl is configured)

**Example Teams card:**

```
┌─────────────────────────────────────┐
│ 📊 Daily Active Users               │
├─────────────────────────────────────┤
│ Executed: 2025-10-22 09:00:15      │
│ Execution Time: 0.34s               │
│ Rows: 1                             │
│                                     │
│ Results:                            │
│ ┌────────────────┐                 │
│ │ active_users   │                 │
│ ├────────────────┤                 │
│ │ 1,247          │                 │
│ └────────────────┘                 │
│                                     │
│ [View Full History →]              │
└─────────────────────────────────────┘
```

### Creating Teams Recipient

1. Navigate to **Recipients** → **Create New Recipient**
2. Fill in details:
   - **Name**: `Alerts Channel`
   - **Type**: Teams
   - **Webhook URL**: Paste your webhook URL
3. Click **Test Webhook** to verify
4. Click **Save**

### Configuring Clickable Links in Teams Notifications

To enable the **"View Query Results"** button in Teams notifications, configure the `BaseUrl` setting in your application:

```csharp
builder.Services.AddBeacon(builder.Configuration, options =>
{
    options.UsePostgreSql(builder.Configuration.GetConnectionString("BeaconContext")!, "beacon");
    options.AddBeaconScheduler<YourScheduler>();
    // Set your Beacon UI base URL (the React app is served at the root)
    options.BaseUrl = "https://yourdomain.com";
});
```

**Or from appsettings.json:**

```json
{
  "Beacon": {
    "BaseUrl": "https://yourdomain.com"
  }
}
```

```csharp
builder.Services.AddBeacon(builder.Configuration, options =>
{
    options.UsePostgreSql(builder.Configuration.GetConnectionString("BeaconContext")!, "beacon");
    options.AddBeaconScheduler<YourScheduler>();
    options.BaseUrl = builder.Configuration["Beacon:BaseUrl"];
});
```

When configured, Teams notifications will include a button that links to:
```
https://yourdomain.com/notifications/details/{notificationId}
```

This allows recipients to:
- View complete query results (not just first 20 rows)
- See execution metrics and history
- Access full notification details
- Explore related query executions

{: .note }
> If `BaseUrl` is not configured, Teams notifications are still sent but without the clickable button. The notification will contain the results table as usual.

📚 [Learn more about BaseUrl configuration →](../getting-started/configuration#base-url-configuration)

### Testing Teams Webhook

Send a test message:

```bash
curl -X POST 'https://outlook.office.com/webhook/...' \
  -H 'Content-Type: application/json' \
  -d '{
    "@type": "MessageCard",
    "text": "Test from Beacon"
  }'
```

You should see the message in your Teams channel immediately.

## Slack Notifications

### Prerequisites

Slack notifications require a webhook URL from your Slack workspace.

### Getting Slack Webhook URL

1. Go to [https://api.slack.com/apps](https://api.slack.com/apps)
2. Click **Create New App** → **From scratch**
3. Enter app details:
   - **App Name**: `Beacon Alerts`
   - **Pick a workspace**: Select your workspace
4. Click **Create App**
5. In the app settings, select **Incoming Webhooks** from the left menu
6. Toggle **Activate Incoming Webhooks** to **On**
7. Click **Add New Webhook to Workspace**
8. Select the channel where notifications should be posted
9. Click **Allow**
10. **Copy the Webhook URL** (format: `https://hooks.slack.com/services/T00000000/B00000000/XXXX...`)

{: .note }
> Keep your webhook URL secure. Anyone with this URL can post messages to your Slack channel.

### Slack Message Format

Slack notifications use Block Kit with superior table formatting:
- **Header**: Query name with professional styling
- **Query Display** (optional): SQL query in code block with syntax highlighting
- **Summary**: "Showing X of Y total records"
- **Results Table**: Rich formatted table with:
  - Up to **20 columns** (vs Teams' 3-column limit)
  - Up to **100 rows** (vs Teams' 10-row limit)
  - **Bold headers** using rich text formatting
  - **Smart column alignment**: Numbers right-aligned, dates center-aligned, text left-aligned
  - **Configurable text wrapping**: Enabled for long text, disabled for compact data
- **Actions**: "View Full Results" button linking to notification details (when BaseUrl is configured)

**Example Slack message:**

```
┌─────────────────────────────────────────────────────┐
│ [Beacon] Production DB - Daily Sales Report     │
├─────────────────────────────────────────────────────┤
│ Query:                                              │
│ ```sql                                              │
│ SELECT product_name, category, quantity,           │
│        revenue, created_at                          │
│ FROM sales WHERE date = CURRENT_DATE                │
│ ORDER BY revenue DESC                               │
│ ```                                                 │
├─────────────────────────────────────────────────────┤
│ Results: Showing 10 of 247 total records           │
│                                                     │
│ ┌────────────┬──────────┬────────┬──────────┬──...│
│ │ Product    │ Category │ Qty    │ Revenue  │ ... │
│ ├────────────┼──────────┼────────┼──────────┼──...│
│ │ Widget Pro │ Hardware │    342 │ $12,456  │ ... │
│ │ Gadget X   │ Hardware │    198 │  $8,234  │ ... │
│ │ Service Y  │ Software │     89 │  $5,672  │ ... │
│ └────────────┴──────────┴────────┴──────────┴──...│
│                                                     │
│ [View Full Results →]                              │
└─────────────────────────────────────────────────────┘
```

### Slack vs Teams Comparison

Slack provides significantly better table formatting than Teams:

| Feature | Microsoft Teams | Slack |
|---------|----------------|-------|
| **Max Columns** | 3 (self-imposed limit) | 20 |
| **Max Rows** | 10 (truncated) | 100 |
| **Header Formatting** | Basic bold text | Rich text with bold style |
| **Column Alignment** | Limited | Full control (left/center/right) |
| **Text Wrapping** | Always enabled | Configurable per column |
| **Cell Formatting** | Plain text only | Rich text with links, emoji |

{: .note }
> **Superior Table Display**: Slack's Table Block feature provides much better formatting for query results with wide tables or many rows. Consider using Slack for data-heavy notifications.

### Creating Slack Recipient

1. Navigate to **Recipients** → **Create New Recipient**
2. Fill in details:
   - **Name**: `Alerts Channel`
   - **Type**: Slack
   - **Webhook URL**: Paste your webhook URL
3. Click **Save**

### Configuring Clickable Links in Slack Notifications

To enable the **"View Full Results"** button in Slack notifications, configure the `BaseUrl` setting in your application:

```csharp
builder.Services.AddBeacon(builder.Configuration, options =>
{
    options.UsePostgreSql(builder.Configuration.GetConnectionString("BeaconContext")!, "beacon");
    options.AddBeaconScheduler<YourScheduler>();
    // Set your Beacon UI base URL (the React app is served at the root)
    options.BaseUrl = "https://yourdomain.com";
});
```

**Or from appsettings.json:**

```json
{
  "Beacon": {
    "BaseUrl": "https://yourdomain.com"
  }
}
```

When configured, Slack notifications will include a button that links to:
```
https://yourdomain.com/notifications/{notificationId}
```

This allows recipients to:
- View complete query results (not just first 100 rows)
- See execution metrics and history
- Access full notification details
- Explore related query executions

{: .note }
> If `BaseUrl` is not configured, Slack notifications are still sent but without the clickable button. The notification will contain the results table as usual.

### Testing Slack Webhook

Send a test message:

```bash
curl -X POST 'https://hooks.slack.com/services/T00000000/B00000000/XXXX...' \
  -H 'Content-Type: application/json' \
  -d '{
    "text": "Test from Beacon",
    "blocks": [
      {
        "type": "section",
        "text": {
          "type": "mrkdwn",
          "text": "This is a test notification from *Beacon*"
        }
      }
    ]
  }'
```

You should see the message in your Slack channel immediately.

### Advanced Table Features

Slack automatically handles:
- **Smart data type detection**: Numbers, dates, booleans, and text are detected and formatted appropriately
- **Automatic alignment**: Numbers right-aligned, dates center-aligned, text left-aligned
- **Text wrapping**: Long text automatically wraps within cells
- **Date formatting**: Dates displayed as YYYY-MM-DD HH:mm:ss
- **Boolean display**: True/False shown as Yes/No

## Jira Notifications

### Prerequisites

Jira notifications require:
- Jira Cloud or Server instance
- API token or credentials
- Project key and issue type

### Creating Jira Recipient

1. Navigate to **Recipients** → **Create New Recipient**
2. Fill in details:
   - **Name**: `Production Incidents`
   - **Type**: Jira
   - **Jira Configuration**: Enter details
     - **Instance URL**: `https://yourcompany.atlassian.net`
     - **Project Key**: `OPS`
     - **Issue Type**: `Incident`
     - **API Token**: Your Jira API token
3. Click **Test Connection**
4. Click **Save**

### Jira Issue Format

Issues created include:
- **Summary**: Query name and timestamp
- **Description**: Query results formatted as table
- **Labels**: `beacon`, `automated`, query name
- **Priority**: Configurable based on result thresholds

## Notification History

### Viewing History

1. Click **Notifications** in left navigation (`/notifications`)
2. View all notification deliveries with:
   - Execution timestamp
   - Query executed
   - Recipients notified
   - Delivery status (Success, Failed)
   - Query results

### Filtering History

Filter by:
- **Subscription**: Show only specific subscription
- **Date Range**: Last 24 hours, week, month, custom
- **Status**: Success, Failed, All
- **Recipient**: Deliveries to specific recipient

### Notification Details

Click any notification to see:
- Complete query results
- Execution metrics (time, row count)
- Recipient delivery status per channel
- Error messages (if delivery failed)
- Query execution plan

## Result Formatting

### Table Size Limits

| Channel | Inline Display | Attachment |
|---------|----------------|------------|
| **Email** | 20 rows (HTML table) | **CSV with ALL rows** (complete dataset) |
| **Teams** | 10 rows, 3 columns (adaptive card) | None |
| **Slack** | 100 rows, 20 columns (table block) | None |
| **Jira** | 50 rows (issue description) | CSV in description |

{: .note }
> **Powerful Reporting**: Email notifications include a CSV attachment with the **complete query result set**, regardless of size. This makes Beacon ideal for scheduled reporting - stakeholders receive full datasets they can analyze in Excel without needing database access.

**Example**: A query returning 5,000 product sales records will show the first 20 rows in the email body, but the CSV attachment will contain all 5,000 rows ready for Excel pivot tables and analysis.

### Column Formatting

Results are automatically formatted:
- Numbers: Comma-separated thousands (1,247)
- Dates: ISO 8601 format (2025-10-22)
- Booleans: true/false
- NULL: Displayed as "NULL"

### Custom Formatting

Use SQL to format results:

**Format numbers:**
```sql
SELECT
    TO_CHAR(revenue, 'FM$999,999,999.00') as formatted_revenue
FROM sales
```

**Format dates:**
```sql
SELECT
    TO_CHAR(created_at, 'YYYY-MM-DD HH24:MI:SS') as formatted_date
FROM events
```

## Delivery Status

### Success

Notification delivered successfully:
- Email: Accepted by email provider (may still bounce)
- Teams: 200 OK response from webhook
- Slack: 200 OK response from webhook
- Jira: Issue created successfully

### Failed

Delivery failed with error:
- Email: Invalid recipient, email provider API error
- Teams: Invalid webhook, channel deleted
- Slack: Invalid webhook, channel deleted, webhook revoked
- Jira: Authentication failed, project not found

**Common failure reasons:**
- Recipient configuration invalid
- External service unavailable
- Authentication expired
- Rate limits exceeded

### Retry Behavior

Beacon does NOT automatically retry failed deliveries:
- Check error message in history
- Fix recipient configuration
- Re-execute subscription manually

{: .note }
> **Design Decision**: No retries prevent notification storms and duplicate alerts. Manual re-execution gives control.

### Real-Time Updates in the UI

In addition to the delivery channels above, the React UI receives real-time updates over a SignalR hub at `/beacon/api/hub`. The `NotificationCreated` event (scoped to the current user) pushes new notifications to the UI as they occur, alongside `JobStatusChanged` and `ApprovalUpdated`.

## Examples

### Example 1: Critical Alert to Teams

**Use Case**: Notify team immediately of production errors

**Subscription:**
- Name: `Critical Error Alert`
- Query: `SELECT * FROM errors WHERE severity = 'CRITICAL' AND created_at > NOW() - INTERVAL '5 minutes'`
- Cron: `*/5 * * * *` (every 5 minutes)
- Recipients: On-Call Channel (Teams)

**Notification:**
- Delivered to Teams within seconds
- Team sees alert card with error details
- Can discuss and assign immediately

### Example 2: Daily Report via Email with Full Results

**Use Case**: Send overnight sales report to management with complete data for Excel analysis

**Subscription:**
- Name: `Daily Sales Report`
- Query: `SELECT product_name, category, SUM(quantity) as units_sold, SUM(revenue) as total_revenue, AVG(price) as avg_price FROM orders WHERE DATE(created_at) = CURRENT_DATE - 1 GROUP BY product_name, category ORDER BY total_revenue DESC`
- Cron: `0 7 * * *` (every day at 7 AM)
- Recipients: Management Team (Email)

**Notification:**
- HTML table in email body (first 20 rows preview)
- **CSV attachment with ALL rows** (complete dataset for Excel)
- Recipients can open CSV directly for pivot tables, charts, and analysis
- Professional formatting

{: .note }
> **Reporting Power**: Unlike other alerting tools that only show summaries, Beacon delivers the complete dataset as a CSV attachment. Perfect for stakeholders who need to analyze data in Excel without database access.

### Example 3: Incident Creation in Jira

**Use Case**: Automatically create incidents for database issues

**Subscription:**
- Name: `Database Health Monitor`
- Query: `SELECT tablename, pg_size_pretty(size) FROM table_sizes WHERE size > 10GB`
- Cron: `0 */6 * * *` (every 6 hours)
- Recipients: DBA Team (Jira)

**Notification:**
- Creates Jira issue in OPS project
- Issue includes table size details
- Assigned to DBA team queue
- Tracked through resolution

### Example 4: Multi-Channel Notification

**Use Case**: Critical alerts to multiple channels for redundancy

**Subscription:**
- Name: `System Down Alert`
- Query: `SELECT COUNT(*) as failures FROM health_checks WHERE status = 'DOWN'`
- Cron: `*/1 * * * *` (every minute)
- Recipients:
  - On-Call Engineer (Teams)
  - DevOps Team (Email)
  - Incident Project (Jira)

**Purpose**: Ensure critical alerts reach team through multiple channels.

## Troubleshooting

### Emails Not Arriving

**Check:**
1. IEmailAdapter is implemented and registered
2. Email adapter configuration is correct (SMTP host, credentials, etc.)
3. Recipient email address is correct
4. Check spam/junk folder
5. Review application logs for email adapter errors

**Debug your email adapter:**
1. Add logging to your `SendEmailAsync` implementation
2. Test email sending outside of Beacon
3. Verify SMTP credentials and server settings
4. Check firewall rules for SMTP ports (usually 587 or 465)

### Teams Webhook Not Working

**Check:**
1. Webhook URL is complete and correct
2. Teams channel still exists
3. Webhook connector not removed
4. URL hasn't expired

**Test webhook manually:**
```bash
curl -X POST 'YOUR_WEBHOOK_URL' \
  -H 'Content-Type: application/json' \
  -d '{"text": "Test"}'
```

If this fails, recreate the webhook in Teams.

### Jira Issues Not Creating

**Check:**
1. Jira credentials are valid
2. Project key exists
3. Issue type is valid for project
4. API token has necessary permissions

**Verify Jira connection:**
- Check Jira API token hasn't expired
- Ensure user has "Create Issue" permission
- Verify project key is uppercase

### Delivery Shows Success But Not Received

**Email:**
- Check spam folder
- Verify recipient email address
- Check SendGrid activity for bounces

**Teams:**
- Verify you're in the correct Teams channel
- Check Teams notification settings
- Refresh Teams client

**Jira:**
- Check correct Jira project
- Verify issue type filter
- Refresh Jira board

## Related Documentation

- [Subscriptions](subscriptions) - Schedule notifications and add recipients
- [Configuration](../getting-started/configuration) - SendGrid and webhook setup

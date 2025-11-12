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

Semantico supports three notification channels:

| Channel | Format | Use Case |
|---------|--------|----------|
| **Email** | HTML table + **CSV attachment with full results** | Scheduled reports, data exports, stakeholder delivery |
| **Microsoft Teams** | Adaptive card with formatted table | Real-time alerts, team collaboration |
| **Jira** | Issue creation with results | Incident tracking, workflow integration |

{: .note }
> **Reporting Feature**: Email is the only channel that delivers complete query results as attachments. This makes it perfect for scheduled reporting where stakeholders need full datasets for analysis in Excel or other tools.

## Email Notifications

### Prerequisites

Email notifications require implementing `IEmailAdapter` in your application:

```csharp
using Semantico.Core.Adapters.Mail;

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
builder.Services.AddSemanticoAdmin(builder.Configuration, options =>
{
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
> **Powerful Reporting Feature**: Email attachments contain the complete query result set, making Semantico perfect for scheduled reports. Recipients can open the CSV directly in Excel for analysis.

**Example email:**

```
Subject: Daily User Count Report - 2025-10-22 09:00:00

Semantico Query Execution

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
https://your-semantico-instance/notifications/12345

---
Sent by Semantico
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
   - **Name**: `Semantico Alerts`
   - **Upload Image**: (optional, Semantico logo)
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
builder.Services.AddSemanticoAdmin(builder.Configuration, options =>
{
    options.AddSemanticoScheduler<YourScheduler>();
    // Set your Semantico UI base URL
    options.BaseUrl = "https://yourdomain.com/semantico";
});
```

**Or from appsettings.json:**

```json
{
  "Semantico": {
    "BaseUrl": "https://yourdomain.com/semantico"
  }
}
```

```csharp
builder.Services.AddSemanticoAdmin(builder.Configuration, options =>
{
    options.AddSemanticoScheduler<YourScheduler>();
    options.BaseUrl = builder.Configuration["Semantico:BaseUrl"];
});
```

When configured, Teams notifications will include a button that links to:
```
https://yourdomain.com/semantico/notifications/details/{notificationId}
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
    "text": "Test from Semantico"
  }'
```

You should see the message in your Teams channel immediately.

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
- **Labels**: `semantico`, `automated`, query name
- **Priority**: Configurable based on result thresholds

## Notification History

### Viewing History

1. Click **Notifications** in left navigation
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
| **Teams** | 20 rows (adaptive card) | None |
| **Jira** | 50 rows (issue description) | CSV in description |

{: .note }
> **Powerful Reporting**: Email notifications include a CSV attachment with the **complete query result set**, regardless of size. This makes Semantico ideal for scheduled reporting - stakeholders receive full datasets they can analyze in Excel without needing database access.

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
- Email: Accepted by SendGrid (may still bounce)
- Teams: 200 OK response from webhook
- Jira: Issue created successfully

### Failed

Delivery failed with error:
- Email: Invalid recipient, SendGrid API error
- Teams: Invalid webhook, channel deleted
- Jira: Authentication failed, project not found

**Common failure reasons:**
- Recipient configuration invalid
- External service unavailable
- Authentication expired
- Rate limits exceeded

### Retry Behavior

Semantico does NOT automatically retry failed deliveries:
- Check error message in history
- Fix recipient configuration
- Re-execute subscription manually

{: .note }
> **Design Decision**: No retries prevent notification storms and duplicate alerts. Manual re-execution gives control.

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
> **Reporting Power**: Unlike other alerting tools that only show summaries, Semantico delivers the complete dataset as a CSV attachment. Perfect for stakeholders who need to analyze data in Excel without database access.

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
2. Test email sending outside of Semantico
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

- [Recipients](recipients) - Configure notification targets
- [Subscriptions](subscriptions) - Schedule notifications
- [Configuration](../getting-started/configuration) - SendGrid and webhook setup
- [Troubleshooting](../troubleshooting/common-issues) - Notification delivery issues

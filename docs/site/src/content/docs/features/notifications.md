---
title: Notifications
description: Deliver query results to recipients via Email, Microsoft Teams, Slack, or Jira, with delivery tracking and history.
---

Notifications deliver query results to recipients via Email, Microsoft Teams, Slack, or Jira.

## Purpose

Notifications allow you to:
- Deliver query results automatically
- Support multiple delivery channels (Email, Teams, Slack, Jira)
- Format results as tables or CSV attachments
- Track delivery status and history
- Handle delivery failures gracefully

## Notification Channels

Beacon supports four notification channels:

| Channel | Format | Use Case |
|---------|--------|----------|
| **Email** | HTML table + **CSV attachment with full results** | Scheduled reports, data exports, stakeholder delivery |
| **Microsoft Teams** | Adaptive card with formatted table | Real-time alerts, team collaboration |
| **Slack** | Rich Table Blocks with 20 columns, 100 rows | Real-time alerts, team collaboration, superior table formatting |
| **Jira** | Issue creation with results | Incident tracking, workflow integration |

:::note[Reporting]
Email is the only channel that delivers complete query results as attachments. This makes it suited to scheduled reporting where stakeholders need full datasets for analysis in Excel or other tools.
:::

:::caution[Upgrading]
Recipients and deliveries are locked down in this version; read the [upgrade notes](/getting-started/configuration/#notification-destinations-optional) before upgrading. In short: recipients outside the destination policy (plain `http://` webhooks, private hosts, an on-premises Jira, retired Office 365 connector URLs, unlisted webhook headers, email display names) stop delivering until they are allowed or re-saved; the system proxy is ignored unless `UseSystemProxy` is on; recipients saved on this version cannot be read by an earlier one, so a rollback means re-entering their secrets; stored secrets are encrypted only when you run `IRecipientSecretEncryptionService` yourself, once every node runs this version (nothing runs it automatically); only Admins and SecurityAdmins manage recipients; changing subscriptions needs the Execute scope; failures show a generic reason; API responses no longer contain destinations.
:::

:::note[Who manages recipients]
Only Admins and SecurityAdmins create, change or delete recipients. A SecurityAdmin changes where a recipient delivers (its type, destination or headers) only when it can read every subscription and data contract the recipient is attached to (`403` otherwise); renaming it is always allowed. Other users can attach existing recipients to subscriptions and data contracts (with write permission and, for API keys, the Execute scope), but never see their destinations; Admins and SecurityAdmins see them masked. Destinations must use `https` and, for Slack, Teams and Jira, the vendor's own hosts; see [Notification Destinations](/getting-started/configuration/#notification-destinations-optional).

Notification calls connect directly by default, and Beacon connects only to an address it has checked. Hosts whose outbound traffic must go through a corporate proxy set `Beacon:Notifications:UseSystemProxy` to `true`. Beacon then checks each destination's addresses before sending, but the proxy resolves the name again and connects wherever its own answer points: in proxy mode the destination guarantee is enforced by the proxy's resolution and egress policy, not pinned by Beacon.

A failed delivery shows a generic reason in the notification history, never the destination's response; a run that fails for some recipients still notifies the others and names the failed recipient ids. Failures recorded by earlier versions may still show the old detail.
:::

### Approval of new and changed destinations

While the [change approval workflow](/features/queries/#approval-workflow) is on and covers recipients
(`ApprovalWorkflow.CoversRecipientDestinations`, on by default — see
[Configuration → Approval Workflow](/getting-started/configuration/#approval-workflow)), where a recipient delivers is a
reviewed change:

- **A recipient created, or whose type, destination or custom headers change, receives nothing until a second person
  approves exactly where it now delivers.** The save is stored and submits a change request (its saver is the
  requester); a create answers with `awaitingApproval: true` and the message *"Submitted for approval: the recipient
  receives no deliveries until a second person approves where it delivers."*, which the recipient dialog shows. An
  Approver (or an Admin) other than the requester approves it on the **Approvals** page under **Recipients**. Approving
  binds to the request the reviewer opened and to the destination the recipient holds then: a recipient changed again
  since is not approved (`409`).
- **Deliveries skip a recipient that waits.** Subscription runs and data-quality alerts deliver to every other recipient;
  the skipped one gets no notification, each skip is logged as a Warning with ids only, and the recipients list and the
  subscription page mark it **Awaiting approval**. A **governed** recipient (one created or changed under the workflow)
  whose stored destination cannot be decrypted is skipped too; a recipient that is not governed fails at send time with
  the "stored destination could not be read" reason, as before.
- **Saves that change nothing where it delivers** (its name, description, body template) submit nothing. Putting back the
  approved destination needs no approval and withdraws the requester's own pending change. A change made while another
  person's change of the same recipient waits is refused (`409`) until that one is approved or rejected; the requester's
  own earlier change is superseded (and they are told). No save supersedes another person's pending change: one that
  would is refused (`409`).
- **A rejected change** leaves the recipient skipped until it is changed back to its approved destination, or saved again
  (which submits it again). The recipients list and the subscription's **Recipients** tab say **Rejected — change it and
  save again**, with the reviewer's comment (`lastApprovalDecision` while no request of it is pending).
- **Attaching an existing recipient** to a subscription or data contract is not reviewed: Editors attach recipients an
  Admin or SecurityAdmin created and a second person approved.
- `IRecipientService.CreateRecipient` and `UpdateRecipient` follow the same rules, with the request's caller as the
  requester: a caller Beacon cannot name is refused (`BeaconForbiddenException`) and nothing is written, and a change
  made while another person's change of the recipient waits comes back unsuccessful (`Success = false`, the reason in
  `Message`) with nothing written. A create submitted for approval says so in its `Message`.

#### What reviewers see

A reviewer must see where data will go without seeing any credential. The change request shows, before and after:

- the **type**, and what **identifies the destination**: a webhook's scheme, host and port, with each path segment
  shown as a mask (so the path's shape is visible) and user info, query values (their names are kept) and a fragment
  masked. Only the parts a known provider documents as not secret are shown: Slack's workspace and webhook ids
  (`hooks.slack.com/services/T…/B…/…`, the token masked); a Teams workflow's (Logic Apps or Power Automate) fixed path
  words and workflow id, its signature (in the query) masked; a legacy Teams connector's group and tenant ids, every
  segment after them masked. Email addresses are shown as they are; a Jira destination shows its site, project and email;
- the **custom header names**, each with a **fingerprint** of its value (12 hex characters), never the value;
- a **fingerprint of the whole definition** (type, destination and headers).

A fingerprint is a short keyed digest (an HMAC under a key derived from `Beacon:EncryptionKey`): equal fingerprints mean
equal values, and without the key it reveals nothing about them. Admins and SecurityAdmins see the same fingerprints for
each recipient on the **Recipients** page, so the requester and the reviewer can compare them out of band (by phone,
say) before the approval.

When the before and after look the same — same type, same destination as shown, same header names — but the
fingerprints differ, the change lies only in a masked part (a new path, token or signature, a new header value). The approval
page says so prominently, and **approving it requires a comment** saying how the reviewer checked the change (`400`
without one; the page requires it too). A request whose proposed destination cannot be read cannot be approved
(`mayApprove: false` with the reason); reject it and submit the change again. Recipient requests are visible to
Approvers (and Admins) only: recipients belong to no project.

#### When a delivery is checked

The check sits in `INotificationService.SendNotification`, the single path to every channel adapter: the very copy of
the destination and headers about to be sent is decrypted, hashed and compared with the recipient's approved hash read
at that moment (archived recipients included). A recipient changed or archived after a run loaded it is therefore never
sent the unapproved copy, and a recipient that no longer exists receives nothing. This also covers a host that calls
`SendNotification` itself with a `RecipientId`: a recipient that waits fails with *"Skipped: where the recipient
delivers waits for approval. Approve its change on the Approvals page, or put back its approved destination."*
(`NotificationFailureReasons.AwaitingApproval`). A delivery without a `RecipientId` — an address no stored recipient
holds — is not a governed recipient's and is not checked, by design.

The approved hash is keyed: an HMAC-SHA256 under a key derived for this purpose from `Beacon:EncryptionKey` (stored with
a version prefix), never a plain hash of the destination, so reading the database does not let anyone test guesses of a
destination or header value. **Replacing the encryption key holds no recipient**: a hash written under a key listed in
`Beacon:PreviousEncryptionKeys` still matches, and the re-encryption run (`IStoredValueReEncryptionService`, see
[Configuration → Replacing the Key](/getting-started/configuration/#replacing-the-key)) recomputes the approved hashes of
governed recipients, and those of their pending requests, under the current key — remove the previous key only after it
reports nothing left. A pending request whose hash was written under a previous key still proposes the destination it
names: a save that leaves where the recipient delivers as it is keeps that request and its requester. A hash of a
destination the recipient no longer holds (an approved destination it has moved away from while a change waits) cannot
be recomputed, as its plaintext is not stored, and nothing tells which key wrote it: it keeps matching only while the
previous key is configured, so the run counts it as an **unresolved key dependency** (logged with the recipient's id)
and does not report the previous keys removable while one remains. Resolve it by deciding the recipient's pending change
— an approval writes the approved hash under the current key — or by saving the recipient back to its approved
destination, then run the re-encryption again. Rotating a recipient's own credential (a new
webhook token, a new header value) is a change of where it delivers: it waits for approval like any other.

#### Runs that notified nobody

A subscription run whose every recipient waits for approval notifies nobody: it is recorded as **Skipped** with *"Skipped:
every recipient waits for approval of where it delivers, so nobody was notified. Approve the recipients' changes on the
Approvals page."* (`NotificationFailureReasons.AllRecipientsAwaitApproval`), a Warning is logged, and a test delivery
answers `409` with that reason. A run that notified some recipients records each skipped one in its comment (`Recipient
7: Skipped: where the recipient delivers waits for approval. …`); the subscription's execution history shows these
reasons readably, naming the held recipients.

A skipped run is **not a baseline**: the result-count trigger (`OnResultCountChange`, `OnResultCountIncrease`) compares a
run with the last run that was not skipped, and a run that notified nobody stores no anomaly-detection baseline. This
holds for every skipped run — also those skipped because the owner was not authorized or because the query's change was
not approved, which run no SQL and store nothing — so a run nobody was told about never hides the next change.

:::caution[Upgrading]
Recipients that exist when you upgrade are not governed: they keep delivering where they do, with no approval and no data
change. Once the workflow is on, a new recipient, or a new destination of an existing one, waits for a second person's
approval before anything is delivered to it — plan for an Approver who is not the Admin or SecurityAdmin making the
change, and for credential rotation (a new token or header value waits too). A change made while the workflow is off is
not reviewed later. Turning `CoversRecipientDestinations` (or the workflow) off again lets the recipients that wait — and
the rejected ones — receive deliveries as they are; startup warns with their count (see
[Configuration → Approval Workflow](/getting-started/configuration/#approval-workflow)). The result-count trigger no
longer compares a run with a skipped one, on any host.
:::

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

See the [complete email adapter implementation](/getting-started/configuration/#email-adapter).

### Email Format

Emails include:
- **Subject**: Query name and execution timestamp
- **Body**:
  - Query description
  - Execution summary (rows returned, execution time)
  - Results formatted as HTML table (first 20 rows)
  - Link to execution history
- **Attachment**: **CSV file with FULL results** (regardless of row count)

:::note[Reporting]
Email attachments contain the complete query result set, so Beacon suits scheduled reports. Recipients can open the CSV directly in Excel for analysis.
:::

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

:::note
If `BaseUrl` is not configured, Teams notifications are still sent but without the clickable button. The notification will contain the results table as usual.
:::

See [BaseUrl configuration](/getting-started/configuration/#base-url-configuration) for details.

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

:::note
Keep your webhook URL secure. Anyone with this URL can post messages to your Slack channel.
:::

### Slack Message Format

Slack notifications use Block Kit with the following table formatting:
- **Header**: Query name
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

Slack provides more table formatting options than Teams:

| Feature | Microsoft Teams | Slack |
|---------|----------------|-------|
| **Max Columns** | 3 (self-imposed limit) | 20 |
| **Max Rows** | 10 (truncated) | 100 |
| **Header Formatting** | Basic bold text | Rich text with bold style |
| **Column Alignment** | Limited | Full control (left/center/right) |
| **Text Wrapping** | Always enabled | Configurable per column |
| **Cell Formatting** | Plain text only | Rich text with links, emoji |

:::note
Slack's Table Block feature provides better formatting for query results with wide tables or many rows. Consider using Slack for data-heavy notifications.
:::

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

:::note
If `BaseUrl` is not configured, Slack notifications are still sent but without the clickable button. The notification will contain the results table as usual.
:::

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
- The stored query results (when the subscription stores results)
- Execution metrics (time, row count)
- Recipient delivery status per channel
- A generic reason when delivery failed (the details are in the server log)

### Who sees a run

The notification list and details show stored runs, including their result rows; the Home activity feed, the Control Tower detail, a task's executions and result history (`GET /tasks/{id}/executions`, `/result-history`) and a subscription's anomaly chart (`GET /subscriptions/{id}/anomaly-chart`) list the same runs. Each run records the data sources it read when it ran. An API key or MCP caller restricted to projects sees a run only when **one** of its projects holds every data source the run read (by the projects' current data sources, the rule [saved-query tools](/features/saved-query-tools/) follow); other runs are not listed, and their details read as not found. Signed-in users with read permission see every run. An API key or MCP caller without a project restriction, like an anonymous request, sees none.

A failed run's comment is shown only when it is a recorded failure reason (for example `Recipient 7: Notification delivery failed: the destination did not respond in time.`); any other stored text is shown as the generic reason, and the details are in the server log.

:::caution[Upgrading]
- A new nullable column records the data sources a run read: `data_source_ids` (`integer[]`) on PostgreSQL, `DataSourceIds` (`nvarchar(max)`, JSON) on SQL Server, added by the `AddRunDataSourceIds` migration. Existing rows are not rewritten.
- Runs recorded before this version have no recorded data sources and are **hidden from project-restricted callers**. Signed-in users still see them.
- API keys and MCP callers without a project restriction no longer see any run (they get an empty list and 404 on the details), as they are already denied every project.
- A run is now visible to a restricted caller when one allowed project holds all of its data sources; a run over a data source shared by several projects is visible from each of them that holds all of the run's sources.
- `INotificationService.GetNotificationDetails` takes a `StoredRunScope`, `GetQueryExecutionHistoryRequest.Scope` is required, and `GetQueryExecutionHistoryDetails` was removed with `QueryExecutionHistoryDetailsData`. `IControlTowerService.GetSubscriptionDetail`, `ITaskService.GetTaskExecutionHistory`, `ITaskService.GetResultCountHistory` and `IAnomalyDetectionService.GetAnomalyChartDataAsync` take a `StoredRunScope` too. Subscription runs carry `QueryResult.DataSourceIds`.
:::

## Result Formatting

### Table Size Limits

| Channel | Inline Display | Attachment |
|---------|----------------|------------|
| **Email** | 20 rows (HTML table) | **CSV with ALL rows** (complete dataset) |
| **Teams** | 10 rows, 3 columns (adaptive card) | None |
| **Slack** | 100 rows, 20 columns (table block) | None |
| **Jira** | 50 rows (issue description) | CSV in description |

:::note[Reporting]
Email notifications include a CSV attachment with the **complete query result set**, regardless of size. Stakeholders receive full datasets they can analyze in Excel without needing database access.
:::

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

:::note[Design Decision]
No retries prevent notification storms and duplicate alerts. Manual re-execution gives control.
:::

### Real-Time Updates in the UI

In addition to the delivery channels above, the React UI receives real-time updates over a SignalR hub at `/beacon/api/hub`. The `NotificationCreated` event (scoped to the current user) pushes new notifications to the UI as they occur, alongside `JobStatusChanged` and `ApprovalUpdated`. The hub is wired automatically — hosts that do not want real-time updates can disable it with `AddBeaconApiServices(x => x.Realtime = false)`, in which case the UI falls back to manual refresh. See [Real-time](/getting-started/installation/#real-time-signalr).

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

- [Subscriptions](/features/subscriptions/) - Schedule notifications and add recipients
- [Configuration](/getting-started/configuration/) - SendGrid and webhook setup

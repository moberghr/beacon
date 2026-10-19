---
title: Notifications
description: Deliver subscription results through five channels (Email, Microsoft Teams, Slack, Jira and a generic webhook) to vetted, encrypted recipients, with a delivery history.
---

When a subscription run [notifies](/features/subscriptions/#which-runs-notify), Beacon sends the result to each of the
subscription's **recipients**. A recipient is one destination on one channel: an email address, a Slack or Teams
webhook, a Jira project, or any HTTPS endpoint. Data-quality alerts use the same recipients.

## Channels

| Channel | What Beacon sends | Inline rows | Attachment |
|---|---|---|---|
| **Email** | An HTML email through the host's `IEmailAdapter` | The preview rows, as an HTML table | CSV or XLSX, when the subscription includes one |
| **Teams** | An Adaptive Card (1.5) posted to a Teams webhook | First 10 rows, first 3 columns | None |
| **Slack** | A Block Kit message posted to an incoming webhook, the rows as a fixed-width text table in a code block | First 25 rows, first 5 columns, each column cut to 20 characters | None |
| **Jira** | An issue (type Task) or a comment on it, through the Jira Cloud REST API | The preview rows, as a table | None |
| **Webhook** | A JSON `POST` to any allowed HTTPS URL | The preview rows, as JSON | None |

What every channel gets from a run:

- **Title.** `[Beacon] {data source} - {query name}` (Email subject, Slack header; the Teams card shows the data source
  and query name).
- **Preview rows.** A short preview of the result's first rows (at most 20), cut further by the subscription's **Max
  rows**, and then by the channel's own limits above. The total row count is always stated.
- **Query text**, only when the subscription has **Show query text in notification** on.
- **Anomaly details** (Slack and webhook), when [anomaly detection](/features/anomaly-detection/) flagged the run.
- **A link back to Beacon**, `{BaseUrl}/notifications/{id}`; see [Links back to Beacon](#links-back-to-beacon).

Rows are [PII-masked](/features/subscriptions/#what-a-run-reads-and-delivers) before they reach any channel, the
attachment included.

### Attachments

A subscription with **Include results as attachment** builds one file per run, **CSV** or **XLSX** as its **Attachment
format** says. The file holds the result's rows up to the subscription's **Max rows** (and never more than
`Beacon:Query:MaxExecutionRows`, since a larger result fails the run), PII-masked like every other row. Only email
delivers it: Teams, Slack and webhooks ignore it, and Jira logs a warning and posts its comment without it.

### Links back to Beacon

Messages link to the run's notification page as `{BaseUrl}/notifications/{id}` (Teams **View Query Results**, Slack
**View Full Results**, the webhook's `notificationUrl`). Without a base URL, messages carry no link.

The React app is served under `/beacon`, so **the base URL must include `/beacon`**:
`https://beacon.example.com/beacon`, not `https://beacon.example.com`. The host sets it in code:

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    options.BaseUrl = builder.Configuration["Beacon:BaseUrl"]; // e.g. https://beacon.example.com/beacon
});
```

An Admin can change it at runtime under **Admin Settings** → **Base URL** (see [Admin Settings](/features/admin-settings/)).

## Recipients

Recipients are managed on the **Recipients** page (`/beacon/recipients`). Only an Admin or a **SecurityAdmin** creates,
changes or deletes them (`POST`, `PUT`, `DELETE /beacon/api/recipients`). Everyone else with Write on a subscription or
data contract can attach existing recipients to it (an API key also needs the Execute scope), but never sees their
destinations; Admins and SecurityAdmins see them masked. A SecurityAdmin changes where a recipient delivers (its type,
destination or headers) only when it can read every subscription and data contract the recipient is attached to (`403`
otherwise); renaming is always allowed.

Click **Add recipient** and fill in:

| Field | What it holds |
|---|---|
| **Notification type** | Email, Slack, Teams, Jira or Webhook. |
| **Name** | How the recipient is listed and picked. |
| Destination | Depends on the type; see the table below. |
| **Description** | Optional. |
| **Custom headers (JSON)** | Webhook only: a JSON object of HTTP headers (see [Webhook](#webhook)). |
| **Body template** | Webhook only: a custom request body (see [Body templates](#body-templates)). |

| Type | Destination field | Example |
|---|---|---|
| Email | **Email address** | `ops@example.com` (a bare address, no display name) |
| Slack | **Slack webhook URL** | `https://hooks.slack.com/services/T…/B…/…` |
| Teams | **Teams webhook URL** | A Teams Workflows webhook URL (see [Microsoft Teams](#microsoft-teams)) |
| Jira | **Jira connection string** | `example;OPS;bot@example.com;<api-token>` |
| Webhook | **Webhook URL** | `https://hooks.example.com/beacon` |

There is no test button: create the recipient, attach it to a subscription, and use **Test now** on the subscription to
send a delivery.

Destinations and header values are secrets: they are encrypted when saved, shown masked, and never returned by the API
in full. Recipients saved before Beacon encrypted them keep working until re-saved or encrypted with
`IRecipientSecretEncryptionService`, unless the host sets `Beacon:Notifications:RequireEncryptedSecrets`. A recipient whose stored destination cannot be decrypted (saved under another encryption key) is marked **Re-enter
destination**; open it and enter the destination again.

### Destination policy

Every destination is checked when it is saved and again when Beacon connects
(`Beacon:Notifications`, see [Notification Destinations](/getting-started/configuration/#notification-destinations-optional)):

- **HTTPS only**, and no user name or password in the URL.
- **Vendor hosts** for the vendor channels: Slack `hooks.slack.com`; Teams `*.webhook.office.com`, `*.logic.azure.com`
  and `*.environment.api.powerplatform.com`; Jira `*.atlassian.net` and `api.atlassian.com`. `AllowedHosts:{Type}`
  extends a list.
- **Webhooks** may use any public HTTPS host, unless `AllowedHosts:Webhook` is set, which then is the whole list.
- **No private networks.** Private, loopback and link-local addresses are unreachable unless listed in
  `AllowedPrivateNetworks:{Webhook|Slack|Teams|Jira}`; Beacon connects only to an address it has checked.
- **Email** takes bare addresses; `AllowedHosts:Email` limits the domains.
- `DisabledTypes` turns channels off; `UseSystemProxy` sends deliveries through the system proxy, which then decides
  where the name resolves.

A failed delivery shows a generic reason in the history, never the destination's response; the details are in the
server log.

### Approval of new and changed destinations

This applies only while the [approval workflow](/features/approvals/) is on (it is off by default; a host turns it on in
code with `options.EnableApprovalWorkflow(...)`) and covers recipients (`CoversRecipientDestinations`, on by default
once the workflow is on). Then where a recipient delivers is a reviewed change:

- **A recipient created, or whose type, destination or custom headers change, receives nothing until a second person
  approves exactly where it now delivers.** The save is stored and submits a change request (the saver is the
  requester); the dialog says *"Submitted for approval: the recipient receives no deliveries until a second person
  approves where it delivers."* An Approver (or an Admin) other than the requester approves it on the **Approvals** page
  under **Recipients**. A recipient changed again since the reviewer opened the request is not approved (`409`).
- **Deliveries skip a recipient that waits.** Every other recipient is still notified; the skipped one is logged as a
  Warning with ids only, and the recipients list and the subscription's **Recipients** tab mark it **Awaiting
  approval**.
- **Saves that change nothing where it delivers** (name, description, body template) submit nothing. Putting back the
  approved destination needs no approval and withdraws the requester's own pending change. A change made while another
  person's change of the same recipient waits is refused (`409`).
- **A rejected change** leaves the recipient skipped until it is changed back to its approved destination or saved
  again; the recipients list and the **Recipients** tab show **Rejected: change it and save again** with the
  reviewer's comment.
- **Attaching an existing recipient** to a subscription or data contract is not reviewed.
- **Rotating a credential** (a new webhook token, a new header value) is a change of where it delivers and waits for
  approval like any other.

Recipients that existed before the workflow was turned on are not governed and keep delivering. Turning the workflow
(or `CoversRecipientDestinations`) off again lets waiting and rejected recipients receive deliveries as they are; startup
warns with their count.

#### What reviewers see

A reviewer sees where data will go without seeing any credential. The change request shows, before and after:

- the **type**, and what **identifies the destination**: a webhook's scheme, host and port, with each path segment
  masked (so the path's shape is visible) and user info, query values and a fragment masked. Only the parts a provider
  documents as not secret are shown: Slack's workspace and webhook ids (the token masked); a Teams workflow's fixed path
  words and workflow id (its signature masked); a legacy Teams connector's group and tenant ids. Email addresses are
  shown as they are; a Jira destination shows its site, project and email;
- the **custom header names**, each with a **fingerprint** of its value (12 hex characters), never the value;
- a **fingerprint of the whole definition** (type, destination and headers).

A fingerprint is a keyed digest (an HMAC under a key derived from `Beacon:EncryptionKey`): equal fingerprints mean equal
values, and without the key it reveals nothing. Admins and SecurityAdmins see the same fingerprints on the
**Recipients** page, so requester and reviewer can compare them out of band before the approval.

When the before and after look the same but the fingerprints differ, the change lies only in a masked part (a new path,
token, signature or header value). The approval page says so, and **approving it requires a comment** saying how the
reviewer checked the change (`400` without one). Recipient requests are visible to Approvers and Admins only: recipients
belong to no project.

#### When a delivery is checked

The check sits in `INotificationService.SendNotification`, the single path to every channel: the copy of the
destination and headers about to be sent is decrypted, hashed and compared with the recipient's approved hash read at
that moment. A recipient changed or archived after a run loaded it is therefore never sent the unapproved copy, and a
recipient that no longer exists receives nothing. A host that calls `SendNotification` itself with a `RecipientId` gets
the same check; a delivery without a `RecipientId` is not a governed recipient's and is not checked.

The approved hash is an HMAC-SHA256 under a key derived from `Beacon:EncryptionKey`, never a plain hash of the
destination. A hash written under a key listed in `Beacon:PreviousEncryptionKeys` still matches, and the re-encryption
run (`IStoredValueReEncryptionService`, see
[Configuration → Replacing the Key](/getting-started/configuration/#replacing-the-key)) recomputes the approved hashes of
governed recipients and their pending requests under the current key. A hash it cannot recompute (an approved
destination the recipient has since moved away from while a change waits) is reported as an unresolved key dependency:
decide the pending change, or save the recipient back to its approved destination, then run the re-encryption again
before removing the previous key.

#### Runs that notified nobody

A run whose every recipient waits for approval notifies nobody: it is recorded as **Skipped** with *"Skipped: every
recipient waits for approval of where it delivers, so nobody was notified. Approve the recipients' changes on the
Approvals page."*, a Warning is logged, and **Test now** answers `409` with that reason. A run that notified some
recipients names each skipped one in its comment. A skipped run is not a baseline for the result-count trigger or for
anomaly detection.

## Email

Beacon does not send email itself: the host implements `IEmailAdapter` and registers it.

```csharp
using Beacon.Core.Adapters;
using Beacon.Core.Adapters.Mail;

public sealed class SmtpEmailAdapter : IEmailAdapter
{
    public async Task SendEmailAsync(string to, string subject, string body, QueryResultFile? queryResultAttachmentFile)
    {
        // body is HTML. queryResultAttachmentFile, when present, has Name, ContentType and Data (bytes).
        // Send with your provider (SMTP, SendGrid, Amazon SES, ...).
    }
}
```

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    options.AddEmailAdapter<SmtpEmailAdapter>();
});
```

Register an adapter before you create email recipients. The email has the subject
`[Beacon] {data source} - {query name}`, and an HTML body with the query text (when shown), the total row count and the
preview rows as a table; every value in it is HTML-encoded. See also
[Configuration → Email Adapter](/getting-started/configuration/#email-adapter).

## Microsoft Teams

Office 365 connector URLs on `outlook.office.com` are retired and refused by the default destination policy. Use a Teams
**Workflows** webhook: in Teams, create a workflow (Power Automate) that starts when a webhook request is received and
posts the card it receives to your channel, then copy its URL. Workflow URLs are on `*.logic.azure.com` or
`*.environment.api.powerplatform.com`; legacy `*.webhook.office.com` URLs are still accepted.

Beacon posts an Adaptive Card (version 1.5) as the request body: a "Beacon" title, `{data source}: {query name}`, the
query text when shown, and a table of the first 10 rows and 3 columns (headed "First 10 records" when there are more),
plus a **View Query Results** button when a [base URL](#links-back-to-beacon) is set.

## Slack

Create a Slack app with **Incoming Webhooks** on, add a webhook to the channel, and copy its URL
(`https://hooks.slack.com/services/T…/B…/…`). Anyone holding the URL can post to the channel.

Beacon posts a Block Kit message: a header `[Beacon] {data source} - {query name}`, an anomaly block when the run was
flagged, the query in a code block when shown, the line *Results: Showing X of Y total records*, the rows as a
fixed-width text table inside a code block (at most 25 rows and 5 columns, each column cut to 20 characters), and a
**View Full Results** button when a [base URL](#links-back-to-beacon) is set.

## Jira

The destination is one string, **`domain;project-key;email;api-key`**:

```text
example;OPS;beacon-bot@example.com;ATATT3xFfGF0...
https://example.atlassian.net;OPS;beacon-bot@example.com;ATATT3xFfGF0...
https://api.atlassian.com/ex/jira/<cloud-id>;OPS;beacon-bot@example.com;ATATT3xFfGF0...
```

The email and API token are a Jira Cloud account's that may create issues in the project. Beacon uses the Jira Cloud
REST API; an on-premises Jira is reachable only when the host allows its host (`AllowedHosts:Jira`) and network
(`AllowedPrivateNetworks:Jira`).

A delivery creates an issue of type **Task** in the project, with the summary `BEACON: {query name}`, the label
`Beacon` and priority **Medium**, its description holding the total row count, the query text when shown and the preview
rows as a table; the same content is added as a comment. A later delivery adds a comment to that issue when Beacon finds
it, and creates a new one otherwise. Jira gets no attachment.

## Webhook

A webhook recipient receives an HTTPS `POST` with a JSON body (`Content-Type: application/json`). By default the body is:

```json
{
  "timestamp": "2026-10-15T09:00:04.512Z",
  "source": "Beacon",
  "subscriptionName": "Unpaid completed orders",
  "subscriptionId": 42,
  "dataSourceName": "Orders DB",
  "sqlQuery": "SELECT id, status, payment_status FROM orders WHERE ...",
  "totalRecords": 3,
  "executionTimeMs": 184.2,
  "previousResultCount": 1,
  "timedOut": false,
  "records": [
    { "id": 1017, "status": "completed", "payment_status": "pending" }
  ],
  "anomaly": { "isAnomaly": true, "severity": "Medium", "explanation": "..." },
  "notificationUrl": "https://beacon.example.com/beacon/notifications/981"
}
```

| Field | What it holds |
|---|---|
| `timestamp` | When the message was built (UTC). |
| `source` | Always `Beacon`. |
| `subscriptionName`, `subscriptionId` | The query name the subscription is shown by, and the subscription id. |
| `dataSourceName` | The data source the result came from. |
| `sqlQuery` | The query text; present only when the subscription shows the query. |
| `totalRecords` | The run's full row count. |
| `executionTimeMs` | How long the run took. |
| `previousResultCount` | The row count of the last run that was not skipped; absent on the first run. |
| `timedOut` | Whether the run timed out. |
| `records` | The preview rows (PII-masked), as objects keyed by column name. |
| `anomaly` | `isAnomaly`, `severity`, `explanation`; present only when anomaly detection flagged the run. |
| `notificationUrl` | The link back to Beacon; present only when a [base URL](#links-back-to-beacon) is set. |

Fields without a value are left out.

**Custom headers.** **Custom headers (JSON)** takes a JSON object of string values, at most 20 headers, each value at
most 4,096 characters without control characters:

```json
{ "Authorization": "Bearer <token>" }
```

Only these names are allowed: `Authorization`, `Api-Key`, `X-Api-Key` and `Ocp-Apim-Subscription-Key`, plus the names
the host adds in `Beacon:Notifications:AllowedHeaders`. Headers that control the connection, routing, cookies or cloud
metadata access (`Host`, `Content-Type`, `Cookie`, `X-Forwarded-For`, `Metadata-Flavor` and others) are never allowed.
Header values are encrypted like the destination; when editing, a masked value keeps the stored one, and clearing the
field removes all headers.

### Body templates

A recipient's **Body template** replaces the default message body. The dialog offers it for webhooks; the Teams and
Slack channels honour one too when it is set through the API (`bodyTemplate` on `POST`/`PUT /beacon/api/recipients`),
where the template must produce that channel's own JSON.

Placeholders are written `{{Name}}`, case-insensitive: `{{Timestamp}}`, `{{Source}}`, `{{SubscriptionName}}`,
`{{SubscriptionId}}`, `{{DataSourceName}}`, `{{SqlQuery}}`, `{{TotalRecords}}`, `{{ExecutionTimeMs}}`,
`{{PreviousResultCount}}`, `{{TimedOut}}`, `{{Records}}`, `{{NotificationUrl}}`, and `{{Anomaly.IsAnomaly}}`,
`{{Anomaly.Severity}}`, `{{Anomaly.Explanation}}`.

- `{{Records}}` becomes compact JSON; add `_Pretty` to a placeholder (`{{Records_Pretty}}`) for indented JSON.
- Dates are ISO 8601, booleans `true`/`false`, decimals two places; a missing value is empty.
- Text values are inserted as they are, without JSON escaping: quote string placeholders yourself, and prefer
  `{{Records}}` for row data.
- An unknown placeholder is left as `{{UNKNOWN:Name}}`. If the template cannot be processed, Beacon sends the default
  body.

```json
{
  "text": "{{SubscriptionName}}: {{TotalRecords}} rows",
  "rows": {{Records}},
  "link": "{{NotificationUrl}}"
}
```

## Notification history

**Notifications** (`/beacon/notifications`) lists subscription runs: **When**, **Query**, **Recipients**, **Rows** and
**Status**, filtered by status and paged on the server (20 rows per page, at most 200). The statuses are those of
[Which runs notify](/features/subscriptions/#which-runs-notify): Created, Sent, Silenced, No results, Timeout, Below
threshold, Failed and Skipped.

A notification's page (`/beacon/notifications/{id}`) shows the delivery (type, created and sent times), the recipient,
the query and subscription, the execution time and result count, and the **Stored results** when the subscription
stores result rows.

A failed run's comment is shown only when it is a recorded failure reason (for example `Recipient 7: Notification
delivery failed: the destination did not respond in time.`); any other stored text is shown as a generic reason, and the
details are in the server log.

### Who sees a run

The notification list and pages show stored runs, including their result rows; the Home activity feed, the Control
Tower detail, a task's executions and result history and a subscription's anomaly chart list the same runs. Each run
records the data sources it read, and a run is visible:

- to an **Admin**, always;
- to a **signed-in user**, when one project they may read holds every data source the run read, or when every data
  source it read is openly reachable;
- to an **API key or MCP caller restricted to projects**, only when one of its projects holds every data source the run
  read (by the projects' current data sources). Runs recorded before Beacon kept their data sources are hidden from such
  callers;
- to an API key or MCP caller with no project, or an anonymous request: never.

Runs you may not see are left out of lists, and their pages read as not found.

### Delivery failures

Every recipient of a run is tried; one failing does not stop the others. A run with any failed delivery is recorded as
**Failed**, naming the failed recipient ids with a generic reason. Beacon does not retry a failed delivery: fix the
recipient, then use **Test now** on the subscription or wait for the next run.

### Real-time updates

The React app receives updates over a SignalR hub at `/beacon/api/hub`: `JobStatusChanged` and `ApprovalUpdated`, each
scoped to the current user. A host that does not want them sets
`AddBeaconApiServices(x => x.Realtime = false)`, and the app falls back to manual refresh.

## Troubleshooting

**Email does not arrive.** Check that the host registered an `IEmailAdapter` (`options.AddEmailAdapter<T>()`), the
adapter's own logs and provider settings, the address, and the spam folder.

**Saving a recipient is refused.** The destination is outside the [destination policy](#destination-policy): not HTTPS,
not a vendor host, a private address, a display name in an email address, or a header that is not allowed. The message
says which.

**Teams delivers nothing.** Use a Workflows URL; `outlook.office.com` connector URLs are refused. Check that the
workflow posts the card it receives to the channel.

**Jira delivers nothing.** Check the four parts of the connection string, that the token's account may create issues in
the project, and that the site is on `*.atlassian.net` (or allowed by the host).

**The link in a message leads to a blank or wrong page.** The base URL is missing `/beacon`; see
[Links back to Beacon](#links-back-to-beacon).

**A recipient shows Awaiting approval.** Its destination waits for a second person's approval on the **Approvals**
page.

## Related

- [Subscriptions](/features/subscriptions/): which runs notify and what they carry
- [Approvals](/features/approvals/): the change-review workflow
- [Configuration → Notification Destinations](/getting-started/configuration/#notification-destinations-optional): the
  `Beacon:Notifications` settings
- [Data Quality](/features/data-quality/): contract alerts use the same recipients

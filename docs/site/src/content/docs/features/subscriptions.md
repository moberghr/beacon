---
title: Subscriptions
description: "Run a query on a cron schedule, decide which runs notify whom, and choose what each notification carries: runs execute as the subscription's owner."
---

A subscription runs one query on a schedule and delivers the result to recipients. It decides when the query runs,
which runs notify anyone, what each notification carries, and whether runs open [tasks](/features/tasks/) or check for
[anomalies](/features/anomaly-detection/).

A subscription has no name of its own: lists, notifications and Jira issues use its query's name.

## Create a subscription

Open **Subscriptions** (`/beacon/subscriptions`) and click **New subscription**, or click **Add subscription** on a
query's page. Creating one needs Write on the query (and, for an API key, the Execute scope). The dialog has five
steps.

### 1. Query

| Field | Default | What it does |
|---|---|---|
| **Query** | Required | The query to run (required). |
| **Cron expression** | `0 9 * * *` | When it runs: a standard 5-field cron expression (required). |
| **Query parameters** | None | A value for each of the query's [parameters](/features/queries/#query-parameters), used by every run. Each value must read as the parameter's type. |

Parameter values are bound as data. A value such as `CURRENT_DATE - INTERVAL '7 days'` is the literal text, never
evaluated: write relative dates into the query's SQL instead.

### 2. Notify

| Field | Default | What it does |
|---|---|---|
| **Send notification** | When result count changes | Which runs notify the recipients: **When result count changes**, **Every execution**, or **When result count increases**. |
| **Minimum row count** | none | Notify only when a run returns at least this many rows. |
| **Recipients** | None | Who is notified. Required unless **Create a task** is ticked. Recipients are created on the [Recipients](/features/notifications/#recipients) page. |
| **Create a task and keep it open while the query returns rows** | off | The first run with rows opens a [task](/features/tasks/); later runs update it, and a run with no rows resolves it. This happens on every run, whatever the notification setting. |

### 3. Results

| Field | Default | What it does |
|---|---|---|
| **Max rows** | no limit | The most rows that reach recipients: the inline table and the attachment. The run's record count is always the full count. |
| **Timeout (seconds)** | the deployment's limit | How long each statement of a run may take, the in-memory final query included. Empty uses `Beacon:Query:StatementTimeoutSeconds` (default 120). A longer value than that limit is refused when you save. |
| **Include results as attachment** | off | Attach the result to the notification as **CSV** or **XLSX** (**Attachment format**). Only email delivers attachments. |
| **Show query text in notification** | off | Include the SQL in the message. |
| **Store result rows for later viewing** | off | Keep a preview of the result's first rows on the run's record, shown on the notification's page. |

### 4. Anomaly

**Enable anomaly detection** makes the subscription notify only when a run's row count is unusual against its history,
instead of using the **Send notification** rule. Set the **Detection method**, **Sensitivity**, **Lookback (days)**
(the dialog accepts 7–365, default 30), **Minimum data points** (the dialog accepts 3–100, default 7), and whether to
alert on an unusual increase, decrease or both. See [Anomaly Detection](/features/anomaly-detection/).

### 5. Review

Check the summary and click **Create subscription**. The subscription records you as its [owner](#owner-and-project-access)
and starts running on its schedule.

## Which runs notify

Every run is recorded with a status. Recipients are notified only when the status is **Sent**:

| Status | When |
|---|---|
| **Timeout** | A step did not complete: it ran out of time or failed. |
| **No results** | The query returned no rows. Nobody is notified, whatever the trigger. |
| **Below threshold** | Fewer rows than **Minimum row count**. |
| **Sent** | The trigger fired: **Every execution**; **When result count changes** and the count differs from the last run's; **When result count increases** and it is higher. A run with no earlier run to compare with notifies. With anomaly detection on: an anomaly was found. |
| **Silenced** | The trigger did not fire, or anomaly detection found nothing unusual. |
| **Failed** | Delivery to at least one recipient failed. The others are still notified; the run names the failed recipient ids with a generic reason. |
| **Skipped** | Nothing ran, or nobody could be notified; see [Scheduled runs run as the owner](#scheduled-runs-run-as-the-owner). |

A skipped run is not a baseline: the result-count trigger compares a run with the last run that was not skipped.

## Cron schedules

Five fields: minute, hour, day of month, month, day of week. Beacon validates the expression when you save; the host's
job runner (see [Background jobs](/integrate/background-jobs/)) fires the runs.

| Expression | Runs |
|---|---|
| `*/5 * * * *` | Every 5 minutes |
| `0 * * * *` | Every hour, on the hour |
| `0 */6 * * *` | Every 6 hours |
| `0 9 * * *` | Daily at 09:00 |
| `0 9 * * 1-5` | Weekdays at 09:00 |
| `0 9,13,17 * * *` | At 09:00, 13:00 and 17:00 |
| `*/15 8-18 * * *` | Every 15 minutes from 08:00 to 18:59 |
| `0 9 * * 1` | Mondays at 09:00 |
| `0 9 1 * *` | The first day of every month at 09:00 |
| `0 9 1 1,4,7,10 *` | The first day of every quarter at 09:00 |

## Manage subscriptions

### The Subscriptions list

The list shows **Id**, **Query**, **Query name**, **Schedule**, **Recipients** and **Source** (Direct, or the AI actor
that created it), filtered to **Active** or **Archived** and searchable by query name. It is paged on the server (20 rows
per page, at most 200) and holds only the subscriptions you may read. Click a row to open the subscription.

### The subscription page

The page (`/beacon/subscriptions/{id}`) shows the query configuration, the owner, the notification settings and run
statistics, with the tabs **Recipients**, **Anomaly detection** (shown only when anomaly detection is on) and
**Execution history**.

| Action | What it does | Needs |
|---|---|---|
| **Test now** | Runs the subscription once, now, and delivers to its recipients (`POST /beacon/api/subscriptions/{id}/execute`). It runs as you, not as the owner, and does not change the schedule. | Write and Run |
| **Archive** | Stops the schedule and moves the subscription to the Archived list (`DELETE /beacon/api/subscriptions/{id}`). Its runs stay in the history. | Manage. The button is enabled for Admins; others with Manage use the API. |
| **Reactivate** | Puts an archived subscription back on its schedule (`POST /beacon/api/subscriptions/{id}/reactivate`). | Write. The button is enabled for Admins; others with Write use the API. |
| **Change owner** | Assigns another owner (see below). | Admin or UserAdmin |
| **Add recipient** / **Remove recipient** | On the **Recipients** tab (`POST /beacon/api/subscriptions/{id}/recipients`, `DELETE …/recipients/{recipientId}`). | Write |

There is no pause and no edit of a subscription's schedule, timeout, parameters, trigger or result options. To change
them, create a new subscription and archive the old one. What can change after creation is its recipients, its owner,
and its SLA.

### SLA hours

A subscription's SLA sets how long its open tasks may stay unresolved; the task page counts down against it (24 hours
when none is set). An Admin edits it from the SLA banner on a task page (the **Edit SLA** gear: 1–720 hours, or
**Use default**). Through the API, `POST /beacon/api/subscriptions/{id}/sla` with `{ "slaHours": 8 }` (1–720, or
`null` to clear) needs Write on the subscription.

## Owner and project access

A subscription records its **owner** when it is created: the signed-in creator, or, for a subscription an
[AI actor](/features/ai-actors/) creates, the actor's creator, never a value from the request. The page shows the owner
by display name, or as "User {id}" without one, never a user name or external id; subscriptions created before owners
were recorded show "no owner recorded", and an owner who matches no Beacon user any more shows "unknown user". While
Beacon user management is on, a caller without a Beacon user (a host principal, or a bearer token not bound to a user)
cannot create a subscription (`403`).

A subscription belongs to the projects that hold its query's data sources: it, its runs and their stored results are
visible only through such a project you may use ([Project access](/features/authorization/#project-access)). One you may
not read answers `404`.

### Scheduled runs run as the owner

Every scheduled run runs as the subscription's owner, as they are at that moment: right before any SQL runs, Beacon
checks that the owner may still run saved SQL in one project that holds every data source of the query's steps. When the
owner may not (disabled or archived, removed from a restricted project, or without the role that runs saved SQL) the
run is **skipped**:

- nothing runs against the data sources, no notification is sent, no task is created or updated, and an AI actor is not
  triggered;
- the run is recorded as **Skipped** with "Skipped: owner not authorized.";
- the job does not fail, and a skipped run is not counted as an execution failure (the Home page's activity feed lists
  it as skipped);
- the Control Tower counts it as an unsuccessful run and marks the subscription (`ownerNotAuthorized`), as does the
  subscription's detail.

The subscription page shows a banner until an Admin or UserAdmin assigns an owner who has access. A subscription without
an owner (created before owners were recorded) runs only while every data source of its query is openly reachable;
otherwise it is skipped, and with `Beacon:Authorization:RequireProjectMembership: true` it is always skipped.

Runs are also **Skipped** when, with the [approval workflow](/features/approvals/) on, the query's stored SQL is not its
approved active version (the page shows "The last run was skipped" with the remedy; see
[Queries → Approval workflow](/features/queries/#approval-workflow)), and when every recipient is waiting for approval of
where it delivers (see [Notifications](/features/notifications/#approval-of-new-and-changed-destinations)).

### Change the owner

An Admin or a UserAdmin clicks **Change owner** on the subscription page (in the header, and in the "owner not
authorized" banner), picks a user and saves, or calls `PUT /beacon/api/subscriptions/{id}/owner` with
`{ "userId": 12 }`. The new owner must be an existing, enabled user who may run the subscription's query in one project
that holds all of its data sources; anyone else is refused with `409`, because every run would be skipped.

## What a run reads and delivers

A run executes its query through the same execution gate and read-only path as every other query execution (see
[How a query runs](/features/queries/#how-a-query-runs)):

- **Read-only:** every statement passes the regex guardrail, the read-only parser in the data source's dialect and the
  database-name rule, and runs inside a `READ ONLY` transaction on PostgreSQL and MySQL.
- **Rows:** a statement may return at most `Beacon:Query:MaxExecutionRows` rows (default 100,000). A larger result fails
  the run rather than delivering part of it, so the run's record count (which drives the trigger, the minimum row count,
  anomaly detection and tasks) is always exact. **Max rows** then limits how many of those rows reach recipients.
- **PII masking:** before the rows reach recipients, attachments or the stored result, columns holding PII are masked:
  by the PII names in the SQL, by their own result-column names and by aliases over PII columns, following the PII
  detection setting and custom patterns of the projects that link the query's data sources (the strictest applies, as
  for MCP). With PII detection off in every linking project, rows are delivered as read. With a final query, the steps'
  rows are joined as read and the final query's result is masked instead: every column it fills from a step's PII
  column, by name, by an alias or by an expression over it, and every column when its select list cannot be traced.
  Columns a host data source masks arrive masked and stay masked.

A run that fails is recorded with its status, and the next scheduled run still happens.

## Troubleshooting

**The subscription never runs.** Check that it is Active (not Archived), that the cron expression is what you meant, and
that the host's job runner is running (see [Background jobs](/integrate/background-jobs/)).

**Runs show Skipped.** Read the run's reason: the owner is not authorized (change the owner), the query change is not
approved, or every recipient waits for approval.

**Runs succeed but nobody is notified.** Check the status: **No results**, **Below threshold** and **Silenced** notify
nobody by design. Then check the recipients and the delivery status in [Notifications](/features/notifications/).

**Runs time out.** Narrow or index the query, or raise the subscription's timeout up to the deployment's limit.

## Related

- [Queries](/features/queries/): the SQL a subscription runs
- [Notifications](/features/notifications/): channels, recipients and delivery history
- [Tasks](/features/tasks/): work items opened by **Create a task**
- [Anomaly Detection](/features/anomaly-detection/): the Anomaly step
- [Control Tower](/features/control-tower/): health across all subscriptions

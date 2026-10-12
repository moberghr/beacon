---
title: Subscriptions
description: Schedule query execution with cron expressions, set timeouts, and route results to notification recipients.
---

Subscriptions schedule when queries execute and who receives notifications.

## Purpose

Subscriptions allow you to:
- Schedule queries with cron expressions
- Define execution frequency (hourly, daily, weekly, custom)
- Specify query timeout limits
- Configure notification recipients
- Control result storage
- Override query parameters

## Use Cases

- **Hourly Health Checks**: Monitor system metrics every hour
- **Daily Reports**: Generate and deliver reports each morning
- **Weekly Summaries**: Aggregate weekly performance data
- **On-Demand Alerts**: Frequent checks for critical thresholds (every 5-15 minutes)
- **Monthly Reports**: End-of-month business analytics

## Creating a Subscription

### Step 1: Navigate to Subscriptions

1. Click **Subscriptions** in the left navigation (`/subscriptions`)
2. Click **Create New Subscription**

### Step 2: Fill Subscription Details

| Field | Description | Required | Example |
|-------|-------------|----------|---------|
| **Name** | Descriptive name | Yes | `Daily User Count Report` |
| **Description** | Purpose and audience | No | `Sent to DevOps team every morning` |
| **Query** | Query to execute | Yes | Select from dropdown |
| **Cron Expression** | Execution schedule | Yes | `0 9 * * *` |
| **Timeout** | Max execution time (seconds) | Yes | 60 |
| **Store Results** | Save to history | No | ✓ Recommended |
| **Send Notifications** | Notify recipients | No | ✓ If recipients added |

### Step 3: Configure Cron Schedule

The cron expression determines when the query runs.

**Format:**
```
* * * * *
│ │ │ │ │
│ │ │ │ └─── Day of week (0-7, 0 and 7 = Sunday)
│ │ │ └───── Month (1-12)
│ │ └─────── Day of month (1-31)
│ └───────── Hour (0-23)
└─────────── Minute (0-59)
```

**Common expressions:**

| Expression | Description | Frequency |
|------------|-------------|-----------|
| `*/5 * * * *` | Every 5 minutes | 288 times/day |
| `0 * * * *` | Every hour, on the hour | 24 times/day |
| `0 */6 * * *` | Every 6 hours | 4 times/day |
| `0 9 * * *` | Daily at 9:00 AM | 1 time/day |
| `0 9 * * 1` | Every Monday at 9:00 AM | 1 time/week |
| `0 9 1 * *` | First day of month at 9:00 AM | 1 time/month |
| `0 9 * * 1-5` | Weekdays at 9:00 AM | 5 times/week |

:::note
Hover over the cron expression field in Beacon to see a human-readable description, or use [crontab.guru](https://crontab.guru/) for assistance.
:::

### Step 4: Set Timeout

The timeout bounds how long each statement of a run may take. Every statement also stops at the deployment's statement limit, `Beacon:Query:StatementTimeoutSeconds` (default 120 seconds; see [Query Execution Row Cap](/getting-started/configuration/#query-execution-row-cap)): a shorter timeout here applies, and saving a longer one is refused — the dialog warns about a timeout above the default limit. Left empty, the run uses the deployment's limit. A subscription stored with a longer timeout before this check runs with the deployment's limit.

**Guidelines:**
- Simple queries: 30-60 seconds
- Complex aggregations: up to the deployment's limit; pre-aggregate anything slower in the database

:::caution
Queries exceeding timeout will be cancelled and marked as failed in execution history.
:::

### Step 5: Add Recipients

1. In the **Recipients** section, click **Add Recipient**
2. Select recipients from the list
3. Click **Save**

See [delivery channels](/features/notifications/) for details.

### Step 6: Configure Parameters (if applicable)

If your query uses parameters (e.g., `{{start_date}}`), you'll see a **Parameters** section:

1. For each parameter, provide a value or expression
2. Use static values: `2025-01-01`
3. Or use dynamic expressions: `NOW() - INTERVAL '7 days'`

See [query parameters](/features/queries/#query-parameters) for details.

### Step 7: Save Subscription

Click **Save** to create the subscription.

The subscription will execute automatically according to your cron schedule.

## Managing Subscriptions

### View Subscriptions

The Subscriptions page shows:
- Subscription name and query
- Cron schedule (human-readable)
- Next execution time
- Last execution status
- Enabled/disabled state
- Actions (Edit, Execute Now, Disable, Delete)

### Owner and project access

A subscription records its **owner** when it is created: the signed-in creator, or, for a subscription an [AI actor](/features/ai-actors/) creates, the actor's creator — never a value from the request. The subscription page shows the owner by display name, or as "User {id}" without one — never a user name or external id; subscriptions created before owners were recorded show "no owner recorded", and an owner who matches no Beacon user any more shows "unknown user". The detail route returns `hasOwner` and `ownerDisplayName` for this, not the owner's id. While Beacon user management is on, a caller without a Beacon user (a host principal, or a bearer token not bound to a user) cannot create a subscription (`403`).

A subscription belongs to the project that holds its query's data sources: it, its runs and their stored results are visible only through such a project the caller may use ([Project access](/features/authorization/#project-access)). One you may not read answers `404` and shows the not-found state.

### Scheduled runs run as the owner

Every scheduled run runs as the subscription's owner, as they are at that moment: right before any SQL runs, Beacon checks that the owner may still run saved SQL in one project that holds every data source of the query's steps. When the owner may not — the owner was disabled or archived, lost their membership of a restricted project, or lost the role that lets them run saved SQL — the run is **skipped**:

- nothing runs against the data sources, no notification is sent, no task is created or updated, and an AI actor is not triggered;
- the run history records the run with the status **Skipped** (`notificationStatus` `8`) and the reason "Skipped: owner not authorized.";
- the job itself does not fail, and a skipped run is not reported as an execution failure (the Home page's failures leave it out; its activity feed lists the run as skipped, and the uptime strip shows it as a warning);
- the Control Tower counts it as an unsuccessful run and marks the subscription (`ownerNotAuthorized` on its health row), and so does the subscription's detail (`ownerNotAuthorized`).

The subscription page then shows a banner saying that scheduled runs are skipped until an Admin assigns an owner who has access, and marks the owner **not authorized**. Skipped runs carry the **Skipped** label in the execution history, the notifications list, and the Control Tower.

A subscription without an owner (created before owners were recorded) runs only while every data source of its query is openly reachable (linked to an open project, or unlinked and open); otherwise it is skipped, and with `Beacon:Authorization:RequireProjectMembership: true` it is always skipped. On a host without Beacon user management, an owner who has no Beacon user runs as the system, as before.

**Execute Now** runs as you, the caller, not as the owner: it needs your access to run the query, and answers `403` without it.

### Change the owner

An Admin or a UserAdmin assigns an owner with **Change owner** on the subscription page (in the header, and in the "owner not authorized" banner): pick a user and save. The same change is `PUT /beacon/api/subscriptions/{id}/owner` with `{ "userId": 12 }` (the user's id in the user directory) — for instance for a subscription created before owners were recorded, or one whose owner left. The new owner must be an existing, enabled user who may run the subscription's query: run saved SQL in one project that holds all of its data sources. A user who may not is refused with `409` and a message saying so, because every run would be skipped; the page shows that message and keeps the picker open so you can choose someone else.

### Execute Manually

Test a subscription without waiting for the schedule:

1. Click **Execute Now** (play icon) on the subscription row
2. Execution runs immediately
3. Check notification delivery
4. Review execution history

:::note
Manual executions don't affect the cron schedule. Scheduled executions continue as configured.
:::

### Enable/Disable Subscription

Temporarily pause a subscription:

1. Click **Disable** (pause icon) on the subscription row
2. Subscription stops executing on schedule
3. Click **Enable** to resume

**Use cases for disabling:**
- Maintenance windows
- Database migrations in progress
- Recipient is out of office
- Query needs modification

### Edit Subscription

1. Click **Edit** (pencil icon) on the subscription row
2. Modify schedule, timeout, or recipients
3. Click **Save**

Changes take effect on the next scheduled execution.

### Delete Subscription

1. Click **Delete** (trash icon) on the subscription row
2. Confirm deletion
3. Subscription is archived (soft delete)

:::note
Execution history is preserved even after subscription deletion.
:::

## Advanced Scheduling

### Business Hours Only

**Weekdays, 9 AM to 5 PM:**
```
0 9-17 * * 1-5
```

Executes every hour from 9 AM to 5 PM, Monday through Friday.

### Multiple Times Per Day

**At 9 AM, 1 PM, and 5 PM:**
```
0 9,13,17 * * *
```

### Every N Minutes During Specific Hours

**Every 15 minutes between 8 AM and 6 PM:**
```
*/15 8-18 * * *
```

### End of Month

**Last day of month at 11 PM:**
```
0 23 L * *
```

:::caution
Standard cron doesn't support `L` (last day). Use `0 23 28-31 * *` and handle logic in query.
:::

### Quarterly Reports

**First day of quarter at 9 AM:**
```
0 9 1 1,4,7,10 *
```

Runs January 1, April 1, July 1, and October 1.

## Subscription Parameters

### Static Parameter Values

Provide fixed values for parameters:

**Parameter:** `threshold`
**Value:** `100`

**Query:**
```sql
SELECT COUNT(*) as high_value_transactions
FROM transactions
WHERE amount > {{threshold}}
```

### Dynamic Parameter Expressions

Use SQL expressions for dynamic values:

**Parameter:** `start_date`
**Value:** `CURRENT_DATE - INTERVAL '7 days'`

**Parameter:** `end_date`
**Value:** `CURRENT_DATE`

**Query:**
```sql
SELECT DATE(created_at) as date, COUNT(*) as orders
FROM orders
WHERE created_at >= '{{start_date}}'
  AND created_at < '{{end_date}}'
GROUP BY DATE(created_at)
```

## Execution Behavior

### When Subscriptions Run

Beacon schedules jobs through the `IBeaconScheduler` abstraction — the host supplies the job runner (we recommend [Moberg Warp](https://moberghr.github.io/warp/); Quartz.NET also works):
- Cron expressions are evaluated according to your scheduler configuration
- Execution timing depends on your scheduler implementation
- Multiple subscriptions can execute concurrently (depending on scheduler worker configuration)

### Execution Order

When multiple subscriptions trigger simultaneously:
- Executions run in parallel (your scheduler's worker pool)
- Long-running queries don't block others

### What a Run Reads and Delivers

A run executes its query's steps through the same execution gate and read-only path as every other query execution (see [How a query runs](/features/queries/#how-a-query-runs)):

- **Read-only:** every statement passes the regex guardrail, the read-only parser in the data source's dialect and the database-name rule, and runs inside a `READ ONLY` transaction on PostgreSQL and MySQL.
- **Rows:** a statement may return at most `Beacon:Query:MaxExecutionRows` rows (default 100,000); the cap is written into the SQL, and on PostgreSQL, MySQL, SQL Server, Azure Synapse and Snowflake the read stops one row past it, so no more are fetched (BigQuery and Databricks read the capped statement's result whole). A larger result fails the run rather than delivering part of it, with a message naming `Beacon:Query:MaxExecutionRows` (raise it as far as the host's memory allows, or filter the query), so the run's record count (which drives *notify on change*, minimum-row rules, anomaly detection and tasks) is always exact. **Max Rows** then limits how many of those rows reach recipients, attachments and the stored result.
- **PII masking:** before the rows reach recipients, attachments, notifications or the stored result, columns holding PII are masked — by the PII names in the SQL, by their own result-column names and by aliases over PII columns — following the PII detection setting and custom patterns of the projects that link the query's data sources (the strictest applies, as for MCP). With PII detection off in every linking project, rows are delivered as read.

### Failure Handling

If a query execution fails:
- Error is logged in execution history
- Notification includes error message
- Next scheduled execution still occurs
- Retry behavior is up to your scheduler implementation — we recommend leaving automatic retries off for query subscriptions to prevent cascading failures

## Monitoring Subscriptions

### Execution History

View all past executions:

1. Click **Notifications** in left navigation
2. Filter by subscription or date range
3. Review:
   - Execution timestamp
   - Query results
   - Recipients notified
   - Delivery status
   - Errors (if any)

### Next Execution Time

The Subscriptions page shows:
- **Next Run**: When subscription will execute next
- **Last Run**: When it last executed
- **Status**: Success, Failed, or Pending

Use this to verify cron expressions are correct.

## Examples

### Example 1: Hourly Health Check

**Subscription:**
- Name: `Database Connection Monitor`
- Query: `Check Active Connections`
- Cron: `0 * * * *` (every hour)
- Timeout: 30 seconds
- Recipients: DevOps Team (Teams)

**Purpose**: Alert if connection count approaches limit.

### Example 2: Daily Morning Report

**Subscription:**
- Name: `Yesterday's Sales Summary`
- Query: `Daily Revenue Report`
- Cron: `0 8 * * *` (every day at 8 AM)
- Timeout: 120 seconds
- Recipients: Sales Team (Email with CSV)

**Purpose**: Deliver overnight sales data to management.

### Example 3: Critical Alert (Every 5 Minutes)

**Subscription:**
- Name: `Critical Error Monitor`
- Query: `Recent Critical Errors`
- Cron: `*/5 * * * *` (every 5 minutes)
- Timeout: 30 seconds
- Recipients: On-Call Engineer (Teams + Email)

**Purpose**: Immediate notification of production errors.

### Example 4: Weekly Summary (Monday Mornings)

**Subscription:**
- Name: `Weekly User Growth Report`
- Query: `New Users Last Week`
- Cron: `0 9 * * 1` (Monday at 9 AM)
- Timeout: 60 seconds
- Recipients: Product Team (Email)

**Purpose**: Weekly tracking of user acquisition.

## Troubleshooting

### Subscription Not Executing

**Check:**
1. Subscription is enabled (not paused)
2. Cron expression is valid
3. Next execution time shows correct schedule
4. Your job scheduler service is running

**Verify Scheduler:**
Check application logs for your scheduler service status and any errors.

### Execution Timeout

If queries consistently timeout:
1. Increase timeout setting
2. Optimize query performance (add indexes)
3. Reduce result set size
4. Split into multi-step query

### Missing Notifications

**Check:**
1. Recipients are added to subscription
2. "Send Notifications" is enabled
3. Notification delivery status in history
4. Recipient configuration (email, Teams webhook)

**Review notification history:**
1. Click **Notifications**
2. Find failed delivery
3. Check error message

## Related Documentation

- [Queries](/features/queries/) - Create and manage queries
- [Notifications](/features/notifications/) - Configure delivery channels and understand notification delivery
- [Anomaly Detection](/features/anomaly-detection/) - Alert on unusual execution patterns

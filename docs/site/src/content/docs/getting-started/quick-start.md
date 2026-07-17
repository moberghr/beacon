---
title: Quick Start
description: "Create your first Beacon database alert end to end: data source, query, subscription, and notification."
---

Create your first database alert end to end.

:::note[Prerequisites]
Beacon must be running. Either run the `Beacon.SampleProject` host or embed Beacon in your own app — see the [Installation Guide](/getting-started/installation/). You should be able to reach the React app at the root URL `/`.
:::

## Overview

In this guide, you'll:

1. Open the Beacon app and sign in
2. Add a data source (the database you want to monitor)
3. Define a simple query
4. Test query execution
5. Create a subscription with a cron schedule
6. Add a recipient for notifications
7. Trigger and verify your first notification

**Estimated time**: ~30 minutes

## Step 1: Open Beacon and Sign In

1. Start the host:

   ```bash
   dotnet run --project Beacon.SampleProject --no-launch-profile
   ```

   (Or run your own app with `dotnet run`.)

2. Open your browser to the root URL — the React SPA is served at `/`:
   - https://localhost:7187/ (or http://localhost:5296/)

3. **First run:** Beacon shows a setup flow that creates the initial admin user — set the admin email and password. There are no default credentials.

4. On later runs, sign in at the `/login` route with the account you created.

:::note
If you're developing the frontend, run the Vite dev server with `npm run dev --prefix src/Beacon.UI/web` and open http://localhost:5173 — it proxies API calls to the running host.
:::

## Step 2: Add a Data Source

A **data source** is a database or API endpoint you want to monitor. Beacon supports nine connectors: PostgreSQL, SQL Server, MySQL, Google BigQuery, Snowflake, Databricks, Azure Synapse, AWS CloudWatch, and generic REST APIs.

1. Open **Data Sources** in the navigation
2. Click **Add Data Source**
3. Fill in the details:
   - **Name**: `My Application Database`
   - **Type**: select your connector (e.g. PostgreSQL)
   - **Connection String**: enter the connection string (encrypted at rest with your `Beacon:EncryptionKey`)

**Connection String Examples:**

**PostgreSQL:**
```
Host=your-db-host;Database=your-db;Username=readonly;Password=your-password
```

**SQL Server:**
```
Server=your-server;Database=your-db;User Id=readonly;Password=your-password;TrustServerCertificate=True
```

**MySQL:**
```
Server=your-server;Database=your-db;Uid=readonly;Pwd=your-password
```

4. Click **Test Connection** to verify connectivity
5. Click **Save**

:::tip
Use a read-only database user for monitoring queries to prevent accidental data modifications.
:::

## Step 3: Create a Query

A **query** defines what data you want to monitor.

1. Open **Queries** in the navigation
2. Click **Create New Query**
3. Fill in the details:
   - **Name**: `Daily New Users`
   - **Description**: `Count of new users registered today`

4. Add a **query step**:
   - **Step Name**: `Count Users`
   - **Data Source**: select `My Application Database`
   - **SQL**:
     ```sql
     SELECT COUNT(*) as new_users
     FROM users
     WHERE created_at >= CURRENT_DATE
     ```
   - **Order**: 1

5. Click **Save**

:::note
Queries can chain multiple steps and join across data sources. Cross-source joins materialize intermediate results in an in-memory engine. See the [Queries Guide](/features/queries/) for multi-step and cross-database queries.
:::

### Data Validation Query Examples

**Alert if required fields are NULL:**
```sql
SELECT
    id,
    email,
    username,
    'Required field is NULL' as issue
FROM users
WHERE email IS NULL
   OR username IS NULL
   OR created_at IS NULL
-- Alert triggers if any rows returned
```

**Detect orphaned records:**
```sql
SELECT
    o.id,
    o.user_id,
    'Order has no associated user' as issue
FROM orders o
LEFT JOIN users u ON o.user_id = u.id
WHERE u.id IS NULL
-- Alert if orphaned orders exist
```

**Check for invalid state combinations:**
```sql
SELECT
    id,
    status,
    payment_status,
    'Completed order without payment' as issue
FROM orders
WHERE status = 'completed'
  AND payment_status != 'paid'
-- Alert if business rule violated
```

**Find duplicate records:**
```sql
SELECT
    email,
    COUNT(*) as duplicate_count
FROM users
GROUP BY email
HAVING COUNT(*) > 1
-- Alert if duplicates found
```

## Step 4: Test Query Execution

Before scheduling, test that your query works:

1. Open your saved query
2. Click **Preview Query Execution**
3. Review the results and verify the data looks correct
4. Note the execution time

:::tip
Always test queries before creating subscriptions to avoid scheduling errors.
:::

## Step 5: Create a Subscription

A **subscription** schedules when your query runs and who gets notified.

1. Open **Subscriptions** in the navigation
2. Click **Create New Subscription**
3. Fill in the details:
   - **Name**: `Daily User Count Alert`
   - **Query**: select `Daily New Users`
   - **Cron Expression**: `0 9 * * *` (daily at 9 AM)
   - **Timeout**: `60` seconds
   - **Store Results**: enabled
   - **Send Notifications**: enabled

**Common Cron Expressions:**

| Expression | Description |
|------------|-------------|
| `0 9 * * *` | Every day at 9:00 AM |
| `0 */6 * * *` | Every 6 hours |
| `*/15 * * * *` | Every 15 minutes |
| `0 9 * * 1` | Every Monday at 9:00 AM |
| `0 0 1 * *` | First day of every month at midnight |

4. Click **Save**

:::note
Use [crontab.guru](https://crontab.guru/) to build and verify cron expressions.
:::

## Step 6: Add a Recipient

**Recipients** define where notifications are delivered.

1. Open **Recipients** (under Notifications) in the navigation
2. Click **Create New Recipient**
3. Choose a notification type:

### Option A: Email

- **Name**: `DevOps Team`
- **Type**: Email
- **Email Address**: `devops@yourdomain.com`

:::caution[Email adapter required]
Email delivery requires an `IEmailAdapter` registered in your host (`options.AddEmailAdapter<...>()`). See the [Configuration Guide](/getting-started/configuration/#email-adapter).
:::

### Option B: Microsoft Teams

- **Name**: `Alerts Channel`
- **Type**: Teams
- **Webhook URL**: `https://outlook.office.com/webhook/...`

### Option C: Jira

- **Name**: `Jira Alerts`
- **Type**: Jira
- **Jira Configuration**: enter your Jira details

4. Click **Save**

## Step 7: Attach the Recipient to the Subscription

1. Open `Daily User Count Alert` under **Subscriptions**
2. In the **Recipients** section, click **Add Recipient**
3. Select `DevOps Team`
4. Click **Save**

## Step 8: Trigger a Manual Execution

1. Open the subscription `Daily User Count Alert`
2. Click **Execute Now**
3. Wait for execution to complete (usually a few seconds)
4. Check the execution status

## Step 9: Verify the Notification

- **Email**: check your inbox
- **Teams**: check your channel for the alert card
- **Jira**: check your project for the new issue

The notification includes the query name and description, execution timestamp, results, and a link back to the details in the Beacon app (when `BaseUrl` is configured).

## Step 10: View Execution History

1. Open **Notifications** in the navigation
2. Find your recent execution and open it to view the executed query, timing, results, recipients, and delivery status

## Next Steps

You've created your first Beacon alert.

### Explore More Features

- [Queries (multi-step & cross-database)](/features/queries/) — chain query steps and join across data sources
- [Data Migration / ETL](/features/data-migration/) — move data between databases with pipelines
- [MCP Server](/features/mcp-server/) — expose Beacon to AI agents over the Model Context Protocol

### Common Use Cases

**Data validation (primary use case):**
- Alert when required fields are NULL
- Detect orphaned records (broken foreign keys)
- Catch invalid state combinations
- Find duplicate records that shouldn't exist
- Validate business-rule compliance

**Database health monitoring:**
- Table sizes exceeding thresholds
- Connection counts approaching limits
- Replication lag warnings
- Slow query detection

**Scheduled reporting with attachments:**
- Daily sales reports as CSV/Excel
- Weekly user-activity summaries
- Monthly financial reports
- Inventory snapshots delivered to stakeholders

**Business metrics and alerts:**
- New user registration alerts
- Transaction volume monitoring
- Customer churn indicators
- Revenue threshold notifications

### Tips for Success

1. **Start simple**: begin with basic queries, add complexity later
2. **Test first**: always preview queries before scheduling
3. **Use descriptive names**: make queries and subscriptions easy to identify
4. **Monitor gradually**: don't create too many alerts at once
5. **Review history**: check execution history to tune query performance

## Troubleshooting

### Query Execution Failed

- Test the query syntax in your database client first
- Verify the data-source user has SELECT permissions
- Increase the subscription timeout if needed
- Review the error message in execution history

### No Notification Received

- Verify the recipient is attached to the subscription
- Check the email adapter configuration (for email)
- Test the Teams webhook URL separately
- Review notification history for error messages

### Cron Not Triggering

- Verify the cron expression (use crontab.guru)
- Confirm the subscription is enabled
- Confirm the scheduler's worker is running — check your job runner's dashboard or logs

### App Not Accessible

- Verify the host is running and reachable at the root URL `/`
- If developing the frontend, confirm the Vite dev server is running, or that `src/Beacon.UI/wwwroot` was rebuilt
- Check the browser console for errors

## Need Help?

- [Feature Documentation](/features/) — detailed guides for all Beacon features
- [Configuration Guide](/getting-started/configuration/) — connection strings, encryption, auth, AI, and scheduling
- [GitHub Discussions](https://github.com/moberghr/beacon/discussions) — ask questions and share ideas

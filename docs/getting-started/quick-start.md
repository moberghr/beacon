---
layout: default
title: Quick Start
parent: Getting Started
nav_order: 2
---

# Quick Start Guide

Create your first database alert in under 30 minutes.

{: .note }
> **Prerequisites**: Semantico must be [installed in your ASP.NET application](installation). You should have the Semantico UI accessible.

## Overview

In this guide, you'll:

1. Access the Semantico UI
2. Create a project (database connection)
3. Define a simple query
4. Test query execution
5. Create a subscription with cron schedule
6. Add a recipient for notifications
7. Trigger and verify your first notification

**Estimated time**: 30 minutes

## Step 1: Access Semantico UI

1. Start your ASP.NET application:
   ```bash
   dotnet run
   ```

2. Open your browser to the Semantico UI path (default: `http://localhost:5000/semantico`)

3. Enter basic authentication credentials (from your `Program.cs`):
   - Username: `admin` (or your configured username)
   - Password: `admin` (or your configured password)

4. You'll see the Semantico dashboard with statistics and navigation

## Step 2: Create a Project

A **project** represents a database connection that you want to monitor.

1. Click **Projects** in the left navigation
2. Click **Create New Project**
3. Fill in the project details:
   - **Name**: `My Application Database`
   - **Description**: `Production database monitoring`
   - **Database Type**: Select your database (PostgreSQL, SQL Server, or MySQL)
   - **Connection String**: Enter your database connection string

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

{: .note }
> **Tip**: Use a read-only database user for monitoring queries to prevent accidental data modifications.

## Step 3: Create a Query

A **query** defines what data you want to monitor.

1. Click **Queries** in the left navigation
2. Click **Create New Query**
3. Fill in the query details:
   - **Name**: `Daily New Users`
   - **Description**: `Count of new users registered today`

4. Add a **Query Step**:
   - **Step Name**: `Count Users`
   - **Project**: Select `My Application Database`
   - **SQL Query**:
   ```sql
   SELECT COUNT(*) as new_users
   FROM users
   WHERE created_at >= CURRENT_DATE
   ```
   - **Order**: 1

5. Click **Save**

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
3. Review the results
4. Verify the data looks correct
5. Note the execution time

{: .note }
> **Pro Tip**: Always test queries before creating subscriptions to avoid scheduling errors.

## Step 5: Create a Subscription

A **subscription** schedules when your query runs and who gets notified.

1. Click **Subscriptions** in the left navigation
2. Click **Create New Subscription**
3. Fill in subscription details:
   - **Name**: `Daily User Count Alert`
   - **Query**: Select `Daily New Users`
   - **Cron Expression**: `0 9 * * *` (Daily at 9 AM)
   - **Timeout**: `60` seconds
   - **Store Results**: ✓ (checked)
   - **Send Notifications**: ✓ (checked)

**Common Cron Expressions:**

| Expression | Description |
|------------|-------------|
| `0 9 * * *` | Every day at 9:00 AM |
| `0 */6 * * *` | Every 6 hours |
| `*/15 * * * *` | Every 15 minutes |
| `0 9 * * 1` | Every Monday at 9:00 AM |
| `0 0 1 * *` | First day of every month at midnight |

4. Click **Save**

{: .note }
> **Cron Help**: Hover over the cron expression field for a human-readable description, or use [crontab.guru](https://crontab.guru/) for help.

## Step 6: Add a Recipient

**Recipients** define where notifications are delivered.

1. Click **Recipients** in the left navigation
2. Click **Create New Recipient**
3. Choose notification type:

### Option A: Email Notification

- **Name**: `DevOps Team`
- **Type**: Email
- **Email Address**: `devops@yourdomain.com`

{: .warning }
> **Email Adapter Required**: Email notifications require implementing `IEmailAdapter` in your application configuration. See [Configuration Guide](configuration#email-adapter).

### Option B: Microsoft Teams

- **Name**: `Alerts Channel`
- **Type**: Teams
- **Webhook URL**: `https://outlook.office.com/webhook/...`

To get your Teams webhook URL:
1. Go to your Teams channel
2. Click **...** → **Connectors** → **Incoming Webhook**
3. Configure and copy the webhook URL

### Option C: Jira

- **Name**: `Jira Alerts`
- **Type**: Jira
- **Jira Configuration**: Enter your Jira details

4. Click **Save**

## Step 7: Add Recipient to Subscription

1. Go back to **Subscriptions**
2. Open `Daily User Count Alert`
3. In the **Recipients** section, click **Add Recipient**
4. Select your recipient (e.g., `DevOps Team`)
5. Click **Save**

## Step 8: Trigger Manual Execution

Test your complete workflow:

1. Open your subscription `Daily User Count Alert`
2. Click **Execute Now** (manual trigger button)
3. Wait for execution to complete (usually 5-10 seconds)
4. Check the execution status

## Step 9: Verify Notification

Check that you received the notification:

- **Email**: Check your inbox for notification from Semantico
- **Teams**: Check your Teams channel for the alert card
- **Jira**: Check your Jira project for the new issue

### What You Should See

The notification will include:
- Query name and description
- Execution timestamp
- Query results (formatted table)
- Link to execution history (if applicable)

## Step 10: View Execution History

1. Click **Notifications** in the left navigation
2. You should see your recent execution
3. Click to view full details:
   - Query executed
   - Execution time
   - Results
   - Recipients notified
   - Delivery status

## Next Steps

🎉 **Congratulations!** You've created your first Semantico alert.

### Explore More Features

<div class="code-example" markdown="1">
📊 **[Multi-Step Queries →](../features/multi-step-queries)**

Chain queries together and aggregate results
</div>

<div class="code-example" markdown="1">
🔄 **[Query Parameters →](../features/parameters)**

Use dynamic placeholders in your queries
</div>

<div class="code-example" markdown="1">
🔗 **[Query Chaining →](../advanced/query-chaining)**

Run queries across multiple databases
</div>

### Common Use Cases

**Data validation (primary use case):**
- Alert when required fields are NULL
- Detect orphaned records (broken foreign keys)
- Catch invalid state combinations
- Find duplicate records that shouldn't exist
- Validate business rule compliance

**Database health monitoring:**
- Table sizes exceeding thresholds (DBA use)
- Connection counts approaching limits
- Replication lag warnings
- Slow query detection

**Scheduled reporting with attachments:**
- Daily sales reports with full data as CSV/Excel
- Weekly user activity summaries
- Monthly financial reports
- Inventory snapshots delivered to stakeholders
- Analytics reports without database access

**Business metrics and alerts:**
- New user registration alerts
- Transaction volume monitoring
- Customer churn indicators
- Revenue threshold notifications

### Tips for Success

1. **Start Simple**: Begin with basic queries, add complexity later
2. **Test First**: Always preview queries before scheduling
3. **Use Descriptive Names**: Make queries and subscriptions easy to identify
4. **Monitor Gradually**: Don't create too many alerts at once
5. **Review History**: Check execution history to tune query performance

## Troubleshooting

### Query Execution Failed

- Check query syntax in your database client first
- Verify database user has SELECT permissions
- Check timeout setting (increase if needed)
- Review error message in execution history

### No Notification Received

- Verify recipient is added to subscription
- Check email adapter configuration (for email)
- Test Teams webhook URL separately
- Review notification history for error messages

### Cron Not Triggering

- Verify cron expression is valid (use crontab.guru)
- Check that subscription is enabled
- Verify Hangfire server is running
- Check Hangfire dashboard at `/hangfire`

### UI Not Accessible

- Verify application is running
- Check UI path configuration in `AddBlazorUI("/semantico")`
- Verify basic authentication credentials
- Check browser console for errors

## Need Help?

<div class="code-example" markdown="1">
📖 **[Feature Documentation →](../features/)**

Detailed guides for all Semantico features
</div>

<div class="code-example" markdown="1">
🔧 **[Troubleshooting →](../troubleshooting/common-issues)**

Solutions for common problems
</div>

<div class="code-example" markdown="1">
💬 **[GitHub Discussions](https://github.com/MiBu/semantico/discussions)**

Ask questions and share ideas
</div>

---
layout: default
title: Subscriptions
parent: Features
nav_order: 4
---

# Subscriptions

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

1. Click **Subscriptions** in the left navigation
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

{: .note }
> **Help**: Hover over the cron expression field in Beacon to see a human-readable description. Or use [crontab.guru](https://crontab.guru/) for assistance.

### Step 4: Set Timeout

The timeout determines maximum query execution time.

**Guidelines:**
- Simple queries: 30-60 seconds
- Complex aggregations: 120-300 seconds
- Long-running reports: 300-600 seconds

{: .warning }
> Queries exceeding timeout will be cancelled and marked as failed in execution history.

### Step 5: Add Recipients

1. In the **Recipients** section, click **Add Recipient**
2. Select recipients from the list
3. Click **Save**

📚 [Learn more about Recipients →](recipients)

### Step 6: Configure Parameters (if applicable)

If your query uses parameters (e.g., `{{start_date}}`), you'll see a **Parameters** section:

1. For each parameter, provide a value or expression
2. Use static values: `2025-01-01`
3. Or use dynamic expressions: `NOW() - INTERVAL '7 days'`

📚 [Learn more about Query Parameters →](parameters)

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

### Execute Manually

Test a subscription without waiting for the schedule:

1. Click **Execute Now** (play icon) on the subscription row
2. Execution runs immediately
3. Check notification delivery
4. Review execution history

{: .note }
> Manual executions don't affect the cron schedule. Scheduled executions continue as configured.

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

{: .note }
> Execution history is preserved even after subscription deletion.

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

{: .warning }
> **Note**: Standard cron doesn't support `L` (last day). Use `0 23 28-31 * *` and handle logic in query.

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

Beacon uses your configured `IBeaconScheduler` implementation for job scheduling:
- Cron expressions are evaluated according to your scheduler configuration
- Execution timing depends on your scheduler implementation
- Multiple subscriptions can execute concurrently (depending on scheduler worker configuration)

### Execution Order

When multiple subscriptions trigger simultaneously:
- Executions run in parallel (worker pool)
- Default worker count: CPU core count
- Long-running queries don't block others

### Failure Handling

If a query execution fails:
- Error is logged in execution history
- Notification includes error message
- Next scheduled execution still occurs
- No automatic retries (prevent cascading failures)

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

- [Queries](queries) - Create and manage queries
- [Recipients](recipients) - Configure notification targets
- [Query Parameters](parameters) - Use dynamic values
- [Notifications](notifications) - Understand notification delivery
- [Troubleshooting](../troubleshooting/common-issues) - Common subscription issues

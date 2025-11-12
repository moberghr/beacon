---
layout: default
title: Data Migration
parent: Features
nav_order: 6
---

# Data Migration

Data Migration enables you to extract data from source databases and synchronize it to destination databases using the same powerful query execution layer used for notifications.

## Purpose

Data Migration allows you to:
- Transfer data between databases (same or different database engines)
- Execute multi-step queries across multiple databases for complex data extraction
- Synchronize data with multiple modes (Insert, Upsert, Truncate, Sync with Delete)
- Schedule automated data transfers with cron expressions
- Track execution history with detailed performance metrics
- Handle failures with automatic retry mechanism

## Use Cases

- **Data Warehouse ETL**: Extract data from production databases and load into analytics databases
- **Multi-Tenant Data Sync**: Synchronize data between tenant databases
- **Cross-Database Reporting**: Consolidate data from PostgreSQL, MySQL, and SQL Server databases
- **Backup & Archival**: Copy data to backup databases on a schedule
- **Data Distribution**: Distribute master data to multiple downstream systems
- **Database Migration**: Migrate data between different database engines

## Creating a Migration Job

### Step 1: Navigate to Data Migration

1. Log in to Semantico
2. Click **Data Migration** in the left navigation menu
3. Click **Create New Migration Job**

### Step 2: Fill Basic Information

| Field | Description | Required | Example |
|-------|-------------|----------|---------|
| **Name** | Descriptive migration job name | Yes | `Daily User Sync` |
| **Description** | Purpose of this migration | No | `Sync active users to analytics database` |

### Step 3: Configure Source Query

The source query extracts data from one or more databases using multi-step queries.

#### Single-Step Query

For simple data extraction from one database:

1. Click **Add Query Step**
2. Fill in step details:
   - **Step Name**: Descriptive name (e.g., "Extract Active Users")
   - **Description**: What this step does
   - **Project**: Select source database project
   - **SQL Query**: Write your SELECT query

**Example:**
```sql
SELECT user_id, username, email, created_at, last_login
FROM users
WHERE active = true
  AND last_login > CURRENT_DATE - INTERVAL '30 days'
```

#### Multi-Step Queries

For complex data extraction across multiple databases:

**Step 1 - Extract Users:**
```sql
-- Project: Production PostgreSQL
SELECT user_id, username, email
FROM users
WHERE active = true
```

**Step 2 - Enrich with Orders:**
```sql
-- Project: Orders MySQL
-- Reference previous step results with @result1
SELECT
    u.user_id,
    u.username,
    u.email,
    COUNT(o.order_id) as total_orders,
    SUM(o.amount) as total_spent
FROM @result1 u
LEFT JOIN orders o ON u.user_id = o.user_id
GROUP BY u.user_id, u.username, u.email
```

**Step 3 - Add Analytics:**
```sql
-- Project: Analytics SQL Server
-- Reference any previous step: @result1, @result2
SELECT
    r.*,
    a.page_views,
    a.session_count
FROM @result2 r
LEFT JOIN user_analytics a ON r.user_id = a.user_id
```

{: .note }
> **Multi-Step Features**:
> - Reference previous results using `@result1`, `@result2`, etc.
> - Each step can use a different project/database
> - Mix PostgreSQL, MySQL, and SQL Server in the same pipeline
> - Visual flow diagram shows step dependencies

#### Test Your Query

1. Click **Preview Query** to test execution
2. Review results in the preview panel
3. Verify data structure matches destination table
4. Check for any errors

### Step 4: Configure Destination

| Field | Description | Example |
|-------|-------------|---------|
| **Destination Project** | Target database | Analytics Database |
| **Destination Table** | Target table name | `user_summary` or `schema.user_summary` |
| **Migration Mode** | How to write data | See modes below |

#### Migration Modes

**Insert Only**
- Inserts new rows only
- Skips rows that already exist
- Use for: Append-only logs, historical data

**Upsert (Insert or Update)**
- Inserts new rows
- Updates existing rows based on primary key
- Use for: Maintaining current state, incremental updates

**Truncate and Load**
- Clears destination table completely
- Inserts all rows from source
- Use for: Full data refresh, replacing entire dataset

**Sync with Delete**
- Inserts new rows
- Updates existing rows
- Deletes rows missing from source
- Use for: Perfect synchronization, removing obsolete data

{: .warning }
> **Truncate and Sync with Delete modes permanently delete data.** Test thoroughly before using in production.

### Step 5: Configure Execution Options

| Field | Description | Default | Range |
|-------|-------------|---------|-------|
| **Schedule** | Cron expression for automated execution | Empty (manual only) | Valid cron |
| **Max Retries** | Retry attempts on failure | 3 | 0-10 |
| **Timeout (minutes)** | Maximum execution time | 30 | 1-1440 |
| **Enabled** | Enable automatic scheduled execution | Unchecked | - |
| **Validate Before Execution** | Test query and table before running | Checked | - |

**Common Schedules:**

| Expression | Description |
|------------|-------------|
| `0 2 * * *` | Daily at 2:00 AM |
| `0 */6 * * *` | Every 6 hours |
| `0 0 * * 0` | Weekly on Sunday at midnight |
| `0 0 1 * *` | Monthly on the 1st at midnight |

{: .note }
> Use [crontab.guru](https://crontab.guru/) to build and validate cron expressions.

### Step 6: Advanced Options (Optional)

**Transformation Script**
- Apply data transformations using @result syntax
- Modify column values before writing to destination
- Currently in development

### Step 7: Save Migration Job

Click **Save** to create the migration job.

## Managing Migration Jobs

### View Migration Jobs

The Data Migration page shows all configured jobs with:
- Job name and description
- Source and destination projects
- Destination table
- Schedule (if configured)
- Last execution status
- Enabled/disabled state
- Actions (Execute, Edit, History, Delete)

### Execute Job Manually

Test or run a job on-demand without waiting for the schedule:

1. Click **Execute** (play icon) on the job row
2. Execution starts immediately
3. Monitor progress in execution history
4. Check destination database for results

{: .note }
> Manual executions don't affect the cron schedule. Scheduled executions continue as configured.

### View Execution History

Review past executions with detailed metrics:

1. Click **History** (clock icon) on the job row
2. Or navigate to **Data Migration → History** for all jobs
3. Filter by:
   - Status (Completed, Failed, Running, etc.)
   - Date Range (Start Date, End Date)

**Execution History Shows:**
- Start time and duration
- Status with color-coded indicator
- Rows read from source
- Rows written to destination
- Rows failed (with error details)
- Performance (rows/second)
- Retry attempt number

### Edit Migration Job

1. Click **Edit** (pencil icon) on the job row
2. Modify any configuration
3. Test changes with **Preview Query**
4. Click **Save**

{: .warning }
> Changes to query or destination table structure may break existing jobs. Always test after editing.

### Enable/Disable Job

Temporarily pause scheduled execution:

1. Toggle the **Enabled** switch in the edit page
2. Or use the **Disable** action on the job row
3. Disabled jobs won't execute on schedule
4. Manual execution is still available

**Use cases for disabling:**
- Database maintenance windows
- Destination table schema changes in progress
- Temporary data freeze requirements
- Testing alternative approaches

### Delete Migration Job

1. Click **Delete** (trash icon) on the job row
2. Confirm deletion in the dialog
3. Job is archived (soft delete)
4. Execution history is preserved

## Examples

### Example 1: Daily User Sync

**Scenario**: Sync active users from production to analytics database every night.

**Configuration:**
- **Name**: `Daily Active User Sync`
- **Source Project**: Production PostgreSQL
- **Query**:
  ```sql
  SELECT user_id, username, email, created_at, last_login, subscription_tier
  FROM users
  WHERE active = true
  ```
- **Destination Project**: Analytics PostgreSQL
- **Destination Table**: `analytics.active_users`
- **Mode**: Truncate and Load
- **Schedule**: `0 2 * * *` (daily at 2 AM)
- **Timeout**: 60 minutes

**Purpose**: Provide analytics team with fresh daily snapshot of active users.

### Example 2: Multi-Database Order Summary

**Scenario**: Combine user data from PostgreSQL, orders from MySQL, and analytics from SQL Server.

**Configuration:**
- **Name**: `Multi-DB Order Summary`

**Step 1 - Extract Users:**
```sql
-- Project: Users DB (PostgreSQL)
SELECT user_id, username, email, signup_date
FROM users
WHERE active = true
```

**Step 2 - Join Orders:**
```sql
-- Project: Orders DB (MySQL)
SELECT
    u.*,
    COUNT(o.order_id) as order_count,
    SUM(o.total_amount) as lifetime_value,
    MAX(o.order_date) as last_order_date
FROM @result1 u
LEFT JOIN orders o ON u.user_id = o.user_id
WHERE o.status = 'completed'
GROUP BY u.user_id, u.username, u.email, u.signup_date
```

**Step 3 - Add Web Analytics:**
```sql
-- Project: Analytics DB (SQL Server)
SELECT
    r.*,
    a.total_sessions,
    a.total_pageviews,
    a.avg_session_duration
FROM @result2 r
LEFT JOIN web_analytics a ON r.user_id = a.user_id
```

- **Destination Project**: Data Warehouse
- **Destination Table**: `dwh.customer_360`
- **Mode**: Upsert
- **Schedule**: `0 */6 * * *` (every 6 hours)

**Purpose**: Create unified customer view across systems.

### Example 3: Incremental Log Migration

**Scenario**: Copy application logs from production to archive database, only new entries.

**Configuration:**
- **Name**: `Incremental Log Archive`
- **Source Project**: Production DB
- **Query**:
  ```sql
  SELECT log_id, timestamp, level, message, user_id, request_id
  FROM application_logs
  WHERE timestamp > CURRENT_TIMESTAMP - INTERVAL '1 hour'
    AND level IN ('ERROR', 'WARN')
  ORDER BY timestamp DESC
  ```
- **Destination Project**: Archive DB
- **Destination Table**: `logs.application_errors`
- **Mode**: Insert Only
- **Schedule**: `0 * * * *` (every hour)
- **Timeout**: 15 minutes

**Purpose**: Archive error logs for long-term retention and compliance.

### Example 4: Weekly Tenant Data Sync

**Scenario**: Synchronize master data to multiple tenant databases weekly.

**Configuration:**
- **Name**: `Tenant A Product Sync`
- **Source Project**: Master DB
- **Query**:
  ```sql
  SELECT product_id, sku, name, description, price, category, active
  FROM products
  WHERE active = true
    AND tenant_id = 'tenant_a'
  ```
- **Destination Project**: Tenant A DB
- **Destination Table**: `public.products`
- **Mode**: Sync with Delete
- **Schedule**: `0 0 * * 0` (Sunday at midnight)
- **Timeout**: 30 minutes

**Purpose**: Keep tenant product catalogs in sync with master, removing discontinued items.

## Migration Modes in Detail

### Insert Only Mode

**Behavior:**
- Attempts to insert each row from source
- If row already exists (primary key conflict), skips it
- Continues processing remaining rows
- No updates, no deletes

**Performance:**
- Fastest mode (no lookups required)
- Minimal database load

**Best For:**
- Append-only data (logs, events, history)
- One-time data loads
- When updates never happen

**Example:**
```sql
-- Source: New user registrations from last hour
SELECT user_id, email, signup_date
FROM users
WHERE signup_date > NOW() - INTERVAL '1 hour'
```

### Upsert Mode

**Behavior:**
- Attempts to insert each row
- If row exists (primary key conflict), updates it
- No deletes

**Requirements:**
- Destination table must have a primary key
- Source query must include primary key column(s)

**Best For:**
- Maintaining current state
- Incremental updates
- Slowly changing dimensions

**Example:**
```sql
-- Source: Current user profile data
SELECT user_id, username, email, last_login, profile_updated_at
FROM users
WHERE active = true
```

### Truncate and Load Mode

**Behavior:**
1. Deletes ALL rows from destination table
2. Inserts all rows from source query
3. Atomic operation (transaction-based)

{: .warning }
> **DESTRUCTIVE**: All existing data is deleted before insert. Rollback occurs only if insert fails.

**Best For:**
- Complete data refresh
- Small to medium datasets
- When destination has no dependencies (no foreign keys)

**Example:**
```sql
-- Source: Complete current product catalog
SELECT product_id, name, price, stock_quantity
FROM products
WHERE active = true
```

### Sync with Delete Mode

**Behavior:**
1. Inserts rows that don't exist in destination
2. Updates rows that exist in destination
3. Deletes rows in destination that aren't in source
4. Perfect synchronization

**Requirements:**
- Destination table must have a primary key
- Source query must include primary key column(s)

{: .warning }
> **DESTRUCTIVE**: Deletes rows missing from source. Ensure source query is correct.

**Best For:**
- Perfect replica synchronization
- Master data distribution
- Removing obsolete data automatically

**Example:**
```sql
-- Source: Current active customer list
SELECT customer_id, name, email, status
FROM customers
WHERE status IN ('active', 'pending')
-- Inactive customers will be DELETED from destination
```

## Error Handling & Retry

### Row-Level Error Handling

Migration jobs handle errors at the row level:
- Failed rows are recorded in execution history
- Processing continues for remaining rows
- Status becomes "Partial Success" if some rows fail
- Error limiting: Stops after 100 consecutive failures

**Common Row Errors:**
- Data type mismatch
- Constraint violations (foreign key, check, unique)
- Column count mismatch
- NULL in NOT NULL column

### Automatic Retry

Failed jobs can automatically retry:
- Configure **Max Retries** (0-10) in job settings
- Retry uses same query and parameters
- Execution history tracks retry chain
- Exponential backoff between retries (future enhancement)

**Retry Scenarios:**
- Source database temporarily unavailable
- Destination database connection timeout
- Network interruption
- Query timeout (execution exceeds configured limit)

### Viewing Error Details

1. Navigate to **Data Migration → History**
2. Find failed or partial success execution
3. Click **Error Details** to view:
   - Full error message
   - Exception stack trace
   - Row-specific failures
   - Timestamp of failure

## Performance Tracking

### Execution Metrics

Each execution records:
- **Start Time**: When execution began
- **Completion Time**: When execution finished
- **Duration**: Total execution time (formatted: 1h 23m 45s)
- **Source Rows Read**: Total rows extracted from source
- **Destination Rows Written**: Rows successfully inserted/updated
- **Rows Failed**: Rows that failed to write
- **Throughput**: Rows per second

### Performance Optimization Tips

**For Slow Source Queries:**
- Add indexes on filter columns
- Reduce result set size with WHERE clauses
- Break into smaller multi-step queries
- Increase query timeout if complex aggregations needed

**For Slow Destination Writes:**
- Ensure destination has appropriate indexes
- Use Insert Only mode when possible (fastest)
- Avoid Upsert/Sync modes for large datasets (require lookups)
- Check destination database load during execution

**For Large Datasets:**
- Schedule during off-peak hours
- Increase timeout setting appropriately
- Consider batch processing (multiple jobs with date ranges)
- Monitor row-level errors for constraints

## Validation

### Pre-Execution Validation

When **Validate Before Execution** is enabled, Semantico checks:
1. Source query syntax is valid
2. Source database is accessible
3. Destination table exists
4. Destination database is accessible
5. Column count/types are compatible (basic check)

{: .note }
> Validation runs the query in a transaction and rolls back immediately. No data is written.

### Connection Validation

Test connectivity before saving:
1. Click **Test Query** in source configuration
2. Review preview results
3. Verify destination project is connectable
4. Check destination table exists with correct schema

## Troubleshooting

### Job Not Executing on Schedule

**Check:**
1. Job is enabled (toggle in edit page)
2. Schedule cron expression is valid
3. Next execution time shows correct schedule
4. Application background job service is running

**Verify:**
- Check application logs for job scheduling errors
- Ensure system time zone is configured correctly

### Source Query Fails

**Common Issues:**
- Syntax error in SQL query
- Table or column doesn't exist
- Source database connection failed
- Query timeout (exceeds configured limit)
- Insufficient permissions (need SELECT)

**Solutions:**
- Use **Preview Query** to test before saving
- Check source project connection string
- Verify database user has SELECT permissions
- Increase timeout for complex queries

### Destination Write Fails

**Common Issues:**
- Destination table doesn't exist
- Column mismatch (count or names differ)
- Data type incompatibility
- Constraint violations (foreign key, unique, check)
- Insufficient permissions (need INSERT/UPDATE/DELETE)

**Solutions:**
- Verify destination table schema matches source query columns
- Check destination project connection
- Verify database user has appropriate permissions:
  - Insert Only: INSERT permission
  - Upsert: INSERT, UPDATE permissions
  - Truncate: DELETE, INSERT permissions
  - Sync with Delete: INSERT, UPDATE, DELETE permissions

### Partial Success Status

**When It Happens:**
- Some rows written successfully
- Some rows failed (constraint violations, data errors)
- Execution completed but with errors

**Action:**
1. View execution history
2. Click **Error Details**
3. Review failed row errors
4. Fix data issues in source or schema in destination
5. Re-run job

### Performance Issues

**Symptoms:**
- Execution consistently times out
- Duration much longer than expected
- High rows/second but still slow overall

**Diagnosis:**
1. Check execution history duration trends
2. Review source query execution plan
3. Check destination database load
4. Monitor network latency between databases

**Solutions:**
- Optimize source query (add indexes, reduce complexity)
- Increase timeout setting
- Schedule during off-peak hours
- Split into multiple smaller jobs
- Use faster migration mode (Insert vs Upsert)

## Best Practices

### Query Design

1. **Use WHERE Clauses**: Filter unnecessary data at source
2. **Select Only Needed Columns**: Avoid SELECT *
3. **Test with LIMIT**: Test queries with small datasets first
4. **Index Source Tables**: Ensure filter columns are indexed
5. **Use Multi-Step for Complexity**: Break complex logic into steps

### Scheduling

1. **Off-Peak Hours**: Schedule large migrations during low-traffic periods
2. **Stagger Jobs**: Don't schedule multiple large jobs simultaneously
3. **Match Frequency to Freshness**: Don't over-schedule if data changes slowly
4. **Allow Time Windows**: Use buffers between dependent jobs

### Error Prevention

1. **Test Before Production**: Execute manually several times before enabling schedule
2. **Validate Data Types**: Ensure source and destination schemas match
3. **Use Transactions**: Rely on atomic operations (built-in)
4. **Monitor First Runs**: Watch initial scheduled executions closely
5. **Enable Validation**: Keep "Validate Before Execution" checked

### Monitoring

1. **Check History Regularly**: Review execution history weekly
2. **Set Up Alerts**: Create notifications for failed migrations (future enhancement)
3. **Track Performance**: Monitor duration trends over time
4. **Review Errors**: Don't ignore partial success statuses

### Security

1. **Use Read-Only Users for Source**: Limit permissions to SELECT only
2. **Limit Destination Permissions**: Grant only needed permissions (INSERT/UPDATE/DELETE)
3. **Avoid Sensitive Data**: Don't migrate passwords, tokens, or PII unless necessary
4. **Audit Regularly**: Review job configurations and execution history

## Related Documentation

- [Queries](queries) - Understanding the query execution layer
- [Projects](projects) - Managing database connections
- [Subscriptions](subscriptions) - Scheduling and automation concepts
- [Configuration](../getting-started/configuration) - Connection string reference
- [Troubleshooting](../troubleshooting/common-issues) - Common issues and solutions

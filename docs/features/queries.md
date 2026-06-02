---
layout: default
title: Queries
parent: Features
nav_order: 2
---

# Queries

Queries define the SQL statements you want to execute and monitor.

## Purpose

Queries allow you to:
- Define SQL statements to extract data from your databases
- Use parameters for dynamic query values
- Create multi-step query workflows
- Reuse queries across multiple subscriptions
- Monitor query execution history

## Use Cases

- **Data Validation** (Primary): Alert when data violates business rules or integrity constraints
- **Data Quality Checks**: Detect NULL values, duplicates, orphaned records, invalid states
- **Business Rule Enforcement**: Trigger alerts when data doesn't meet expected criteria
- **Database Health** (Secondary): Monitor database metrics for DBAs (table size, connections, performance)
- **Compliance Reporting**: Generate audit reports on schedules

## Creating a Simple Query

### Step 1: Navigate to Queries

1. Log in to Beacon at the React UI (`/login`)
2. Click **Queries** in the left navigation (`/queries`)
3. Click **Create New Query**

### Step 2: Fill Query Details

| Field | Description | Required | Example |
|-------|-------------|----------|---------|
| **Name** | Descriptive query name | Yes | `Daily Active Users` |
| **Description** | Purpose of this query | No | `Count users active in last 24 hours` |
| **Store Results** | Save execution results | No | ✓ Checked |

### Step 3: Add Query Step

Every query has at least one step. Click **Add Query Step**:

| Field | Description | Required | Example |
|-------|-------------|----------|---------|
| **Step Name** | Name for this step | Yes | `Count Active Users` |
| **Project** | Database to query | Yes | Select your project |
| **SQL Query** | SQL statement | Yes | See examples below |
| **Order** | Execution order | Yes | 1 (for first step) |

**Simple query example:**
```sql
SELECT COUNT(*) as active_users
FROM users
WHERE last_login >= NOW() - INTERVAL '24 hours'
```

### Step 4: Save Query

Click **Save** to create the query.

## Query Examples

### Monitor Table Growth

**PostgreSQL:**
```sql
SELECT
    schemaname,
    tablename,
    pg_size_pretty(pg_total_relation_size(schemaname||'.'||tablename)) AS size,
    pg_total_relation_size(schemaname||'.'||tablename) AS size_bytes
FROM pg_tables
WHERE schemaname = 'public'
ORDER BY size_bytes DESC
LIMIT 10
```

**SQL Server:**
```sql
SELECT
    t.NAME AS TableName,
    s.Name AS SchemaName,
    p.rows AS RowCount,
    CAST(ROUND((SUM(a.total_pages) * 8) / 1024.00, 2) AS NUMERIC(36, 2)) AS SizeMB
FROM sys.tables t
INNER JOIN sys.indexes i ON t.OBJECT_ID = i.object_id
INNER JOIN sys.partitions p ON i.object_id = p.OBJECT_ID AND i.index_id = p.index_id
INNER JOIN sys.allocation_units a ON p.partition_id = a.container_id
LEFT OUTER JOIN sys.schemas s ON t.schema_id = s.schema_id
WHERE t.is_ms_shipped = 0
GROUP BY t.Name, s.Name, p.Rows
ORDER BY SizeMB DESC
```

### Check Database Connections

**PostgreSQL:**
```sql
SELECT
    datname,
    count(*) as connections,
    max(client_addr) as last_client
FROM pg_stat_activity
WHERE datname IS NOT NULL
GROUP BY datname
ORDER BY connections DESC
```

**SQL Server:**
```sql
SELECT
    DB_NAME(dbid) as DatabaseName,
    COUNT(dbid) as NumberOfConnections,
    loginame as LoginName
FROM sys.sysprocesses
WHERE dbid > 0
GROUP BY dbid, loginame
ORDER BY NumberOfConnections DESC
```

### Monitor Query Performance

**PostgreSQL (slow queries):**
```sql
SELECT
    query,
    calls,
    mean_exec_time,
    max_exec_time
FROM pg_stat_statements
WHERE mean_exec_time > 1000  -- Queries slower than 1 second
ORDER BY mean_exec_time DESC
LIMIT 20
```

### Data Validation Examples (Primary Use Case)

**Check for NULL values in required fields:**
```sql
SELECT
    id,
    email,
    username
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
    'Orphaned order - user not found' as issue
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
    payment_status
FROM orders
WHERE status = 'completed'
  AND payment_status != 'paid'
-- Alert if completed orders are not paid
```

**Detect duplicate records:**
```sql
SELECT
    email,
    COUNT(*) as duplicate_count
FROM users
GROUP BY email
HAVING COUNT(*) > 1
-- Alert if duplicate emails exist
```

**Business rule violations:**
```sql
SELECT
    id,
    total_amount,
    discount_amount
FROM orders
WHERE discount_amount > total_amount
-- Alert if discount exceeds total (invalid business rule)
```

**Referential integrity checks:**
```sql
SELECT
    p.id,
    p.category_id,
    'Invalid category reference' as issue
FROM products p
WHERE category_id NOT IN (SELECT id FROM categories)
-- Alert if products reference non-existent categories
```

## Query Parameters

Queries can include dynamic parameters using `{{parameterName}}` syntax.

**Query with parameter:**
```sql
SELECT COUNT(*) as user_count
FROM users
WHERE created_at >= '{{start_date}}'
  AND created_at < '{{end_date}}'
```

When creating a subscription, you'll provide values for `start_date` and `end_date`.

## Multi-Step Queries

Create complex workflows by chaining multiple query steps.

**Example: Cross-database data aggregation:**

**Step 1** - Query PostgreSQL database:
```sql
SELECT customer_id, SUM(amount) as total
FROM orders
WHERE created_at >= CURRENT_DATE
GROUP BY customer_id
```

**Step 2** - Aggregate results:
```sql
SELECT
    COUNT(*) as customers_with_orders,
    SUM(total) as grand_total,
    AVG(total) as average_order
FROM @result1  -- References Step 1 results
```

Cross-database multi-step queries materialize intermediate results into an in-memory SQLite database, then run the final join there.

## Managing Queries

### View Queries

The Queries page shows:
- Query name and description
- Associated project
- Number of subscriptions using this query
- Last execution time
- Actions (Edit, Delete, Preview, View History)

### Edit Query

1. Click **Edit** (pencil icon) on the query row
2. Modify query details or SQL
3. Test changes with **Preview Execution**
4. Click **Save**

{: .note }
> **Tip**: Use **Preview Execution** to test query changes before saving. This shows results without creating execution history.

### Delete Query

{: .warning }
> **Careful**: Deleting a query will disable all subscriptions using it.

1. Click **Delete** (trash icon) on the query row
2. Review impact message (shows affected subscriptions)
3. Confirm deletion

Queries are archived (soft delete) and can be restored if needed.

### Preview Query Execution

Test queries without scheduling:

1. Open your query
2. Click **Preview Query Execution**
3. Review results in table format
4. Check execution time
5. Verify data is as expected

## Query Best Practices

### Performance

**DO:**
- ✓ Use indexes on frequently queried columns
- ✓ Limit result sets with `LIMIT` or `TOP`
- ✓ Filter early with `WHERE` clauses
- ✓ Use appropriate data types
- ✓ Test performance on production-sized data

**DON'T:**
- ✗ Use `SELECT *` (specify columns)
- ✗ Query without indexes
- ✗ Use complex JOINs unnecessarily
- ✗ Return millions of rows
- ✗ Use cursors or loops

### Security

**DO:**
- ✓ Use read-only database users
- ✓ Parameterize dynamic values
- ✓ Validate query results make sense
- ✓ Monitor execution history for anomalies

**DON'T:**
- ✗ Use admin/superuser accounts
- ✗ Include passwords or secrets in queries
- ✗ Allow SQL injection via parameters
- ✗ Query sensitive data unnecessarily

### Reliability

**DO:**
- ✓ Set appropriate timeout values
- ✓ Handle NULL values in aggregations
- ✓ Use defensive SQL (COALESCE, NULLIF)
- ✓ Test edge cases (empty results, errors)

**DON'T:**
- ✗ Rely on unstable temp tables
- ✗ Use database-specific syntax if cross-db needed
- ✗ Forget to handle zero-row results
- ✗ Use non-deterministic functions without intent

## Query Examples by Use Case

### Application Monitoring

**Error rate (last hour):**
```sql
SELECT
    error_type,
    COUNT(*) as error_count,
    MAX(created_at) as last_error
FROM error_logs
WHERE created_at > NOW() - INTERVAL '1 hour'
GROUP BY error_type
ORDER BY error_count DESC
```

**Slow API endpoints:**
```sql
SELECT
    endpoint,
    AVG(response_time_ms) as avg_response,
    MAX(response_time_ms) as max_response,
    COUNT(*) as request_count
FROM api_logs
WHERE created_at > NOW() - INTERVAL '1 hour'
GROUP BY endpoint
HAVING AVG(response_time_ms) > 1000  -- Slower than 1 second
ORDER BY avg_response DESC
```

### Database Health

**Disk space usage (PostgreSQL):**
```sql
SELECT
    pg_database.datname,
    pg_size_pretty(pg_database_size(pg_database.datname)) AS size
FROM pg_database
ORDER BY pg_database_size(pg_database.datname) DESC
```

**Index health (PostgreSQL):**
```sql
SELECT
    schemaname,
    tablename,
    indexname,
    idx_scan,
    idx_tup_read,
    idx_tup_fetch
FROM pg_stat_user_indexes
WHERE idx_scan = 0  -- Unused indexes
ORDER BY pg_relation_size(indexrelid) DESC
```

### Scheduled Reporting Queries

Queries designed for reports delivered as CSV/Excel attachments:

**Daily sales report (complete dataset):**
```sql
SELECT
    p.product_name,
    p.category,
    o.order_date,
    o.customer_id,
    c.customer_name,
    o.quantity,
    o.unit_price,
    o.total_amount
FROM orders o
JOIN products p ON o.product_id = p.id
JOIN customers c ON o.customer_id = c.id
WHERE DATE(o.created_at) = CURRENT_DATE - INTERVAL '1 day'
ORDER BY o.total_amount DESC
-- Full results sent as CSV attachment for Excel analysis
```

**Weekly user activity report:**
```sql
SELECT
    u.username,
    u.email,
    COUNT(a.id) as login_count,
    MAX(a.created_at) as last_login,
    SUM(a.duration_minutes) as total_minutes
FROM users u
LEFT JOIN activity_logs a ON u.id = a.user_id
    AND a.created_at >= CURRENT_DATE - INTERVAL '7 days'
GROUP BY u.id, u.username, u.email
ORDER BY login_count DESC
-- Delivered Monday mornings as CSV for management review
```

**Monthly financial summary:**
```sql
SELECT
    DATE_TRUNC('month', created_at) as month,
    category,
    COUNT(*) as transaction_count,
    SUM(amount) as total_amount,
    AVG(amount) as average_amount,
    MIN(amount) as min_amount,
    MAX(amount) as max_amount
FROM transactions
WHERE created_at >= DATE_TRUNC('month', CURRENT_DATE - INTERVAL '1 month')
  AND created_at < DATE_TRUNC('month', CURRENT_DATE)
GROUP BY DATE_TRUNC('month', created_at), category
ORDER BY category
-- Delivered first day of month with complete data for analysis
```

{: .note }
> **Reporting Power**: These queries return complete datasets that are delivered as CSV attachments. Recipients can open them directly in Excel for pivot tables, charts, and further analysis without needing database access.

## Troubleshooting

### Query Syntax Errors

**Test in database client first:**
```bash
# PostgreSQL
psql -h host -U user -d database -c "SELECT COUNT(*) FROM users"

# SQL Server
sqlcmd -S server -U user -P password -d database -Q "SELECT COUNT(*) FROM users"

# MySQL
mysql -h host -u user -p database -e "SELECT COUNT(*) FROM users"
```

### Query Timeout

If queries time out:
1. Check query execution plan for performance issues
2. Add indexes to improve performance
3. Increase timeout in subscription settings
4. Simplify query or reduce result set

### Permission Errors

Verify user has SELECT permission:
```sql
-- PostgreSQL
SELECT grantee, table_schema, table_name, privilege_type
FROM information_schema.table_privileges
WHERE grantee = 'your_user';
```

## Related Documentation

- [Subscriptions](subscriptions) - Schedule query execution
- [Data Sources](data-sources) - Database connection and project management
- [Data Migration](data-migration) - Cross-database ETL using the same query layer

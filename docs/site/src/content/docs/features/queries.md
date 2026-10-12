---
title: Queries
description: Define SQL statements with parameters and multi-step workflows to validate data and monitor database health.
---

![Query editor](/img/screenshots/query-editor-light.png)

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

:::tip
Use **Preview Execution** to test query changes before saving. This shows results without creating execution history.
:::

### Delete Query

:::caution
Deleting a query will disable all subscriptions using it.
:::

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
- ✗ Name another database than the step's data source

### Database names

A step's SQL may name objects of its own data source's database only. Unqualified and schema-qualified names always work; a name qualified with a database must use the one the data source's connection is configured for, and is refused otherwise — when the query or step is saved, and again before every preview, run and subscription execution:

| Engine | Full name | Must equal |
|---|---|---|
| SQL Server, Azure Synapse | `database.schema.table` (four-part linked-server names are refused) | `Initial Catalog` / `Database` |
| PostgreSQL | `database.schema.table` | `Database` / `DB` (unquoted names fold to lower case) |
| MySQL | `database.table` | `Database` / `Initial Catalog` |
| Snowflake | `database.schema.table` | `db` (unquoted names fold to upper case) |
| Databricks | `catalog.schema.table` | the configured `Catalog` |
| BigQuery | `project.dataset.table` (also inside one backtick-quoted name) | the configured `ProjectId` |

Function and type names follow the same rule. A data source without a configured database accepts no database qualifier, and names resolved only at run time (`IDENTIFIER(...)`, Snowflake `TABLE('...')`) are refused. The final query of a multi-step query runs on the in-memory step results and is not affected. See [Database names in SQL](/features/authorization/#database-names-in-sql).

### How a query runs

Every statement Beacon runs for a query — a preview (stored, draft or paged), a subscription run or its test delivery, an AI actor's check of the query it wrote, a data migration's source read — goes through the same execution gate as the MCP tools, then the data source's read-only path:

1. **The execution gate**, with read-only enforcement always on: a length cap, a refusal of control characters (other than tab and line breaks) and Unicode noncharacters, the regex guardrail, the read-only parser in the data source's dialect (one `SELECT`, no writes, no locking hints, no side-effecting or cross-connection functions), the [database-name rule](#database-names), the host policy for a host data source, and a row cap written into the SQL itself (`LIMIT` / `TOP`). The text judged is the text that runs: placeholders bound as parameters, `--` line comments removed (as the data source's dialect reads them) and line breaks flattened, so a comment line never swallows the clause after it. The regex guardrail reads the statement without its comments — a step may start with `-- Revenue by month` or explain itself with `/* … */`, even naming a write it does not make — while MySQL executable comments (`/*! … */`) and optimizer hints (`/*+ … */`) stay and are judged; the parser always reads the SQL as written. An `END` that closes no `CASE` is refused (the parser would stop reading there), and on SQL Server and Azure Synapse so is an unquoted alias that is a statement keyword, such as `SELECT 1 COMMIT` — quote it (`[COMMIT]`) to use it as a name.
2. **The read-only path** of the provider: on PostgreSQL and MySQL the statement runs inside a `READ ONLY` session and transaction (on PostgreSQL as a batch of exactly one command, so a second statement is refused), so the database itself refuses a write the parser missed. SQL Server has no such mode: the statement runs inside a transaction that is always rolled back, which undoes such a write but does not refuse it, and a statement that ends that transaction fails the run. Azure Synapse and Snowflake run it plainly; BigQuery and Databricks refuse anything their own dialect's parser does not read as a single `SELECT`, and BigQuery runs only what a dry run reports as a `SELECT`. On every engine but PostgreSQL and MySQL a read-only login is the database-level boundary, and the data source pages say so; on PostgreSQL and MySQL the engine's read-only session is defence in depth, and a read-only login is still required ([Read-only guarantees per engine](/features/data-sources/#read-only-guarantees-per-engine)).
3. **Limits:** one statement may return at most `Beacon:Query:MaxExecutionRows` rows (default 100,000; see [Query Execution Row Cap](/getting-started/configuration/#query-execution-row-cap)) — the cap is written into the SQL: a statement's own `LIMIT`, `TOP` or `FETCH` above the cap, `LIMIT ALL` or `NULL` is lowered to it, and an expression, `TOP … PERCENT` or `WITH TIES` is wrapped in a capped derived table (on SQL Server a statement with a `WITH` clause that cannot be lowered is refused); on SQL Server and Azure Synapse a `UNION`, `EXCEPT` or `INTERSECT` without an `ORDER BY` is capped as a derived table too, so its result columns need names (alias them). On PostgreSQL, MySQL, SQL Server, Azure Synapse and Snowflake the read stops one row past the cap; BigQuery and Databricks read the capped statement's result whole. A larger result fails the run with *returned more than N rows (the `Beacon:Query:MaxExecutionRows` limit)* instead of delivering part of it — and a statement may run for `Beacon:Query:StatementTimeoutSeconds` (default 120 seconds). Closing a preview or cancelling a request stops the statement.

The final query of a multi-step query runs on the steps' results in Beacon's in-memory SQLite store, behind the same gate (without the database-name rule — it reaches no database) and the store's own read-only engine settings. A final query that runs out of time fails the run — a preview, a subscription, an AI actor's check and a migration all see a failure, never an empty result.

**When a query is saved**, each step's SQL must already pass that gate, so SQL that could never run is not stored. When Beacon holds metadata for the step's data source, every column the SQL reads from a table in that metadata must exist there; refresh the data source's metadata after adding a column. The same check applies when an AI actor creates or refines a query.

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

:::note[Reporting]
These queries return complete datasets that are delivered as CSV attachments. Recipients can open them directly in Excel for pivot tables, charts, and further analysis without needing database access.
:::

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

A statement may run for `Beacon:Query:StatementTimeoutSeconds` (default 120 seconds; see [Query Execution Row Cap](/getting-started/configuration/#query-execution-row-cap)). If queries time out:
1. Check query execution plan for performance issues
2. Add indexes to improve performance
3. Simplify query or reduce result set

### Returned more than 100000 rows

One statement may return at most `Beacon:Query:MaxExecutionRows` rows (default 100,000), and the message names that setting (a migration's read names its own, `Beacon:Migrations:MaxRows`); a larger result fails instead of being cut short, so a subscription never reports a partial count. Filter or aggregate the step, or raise the setting as far as the host's memory allows. On MySQL a read stopped at the cap can leave its read-only transaction unable to close; the result is then discarded with the same *returned more than N rows* message. A paged preview reads one page and a count when the SQL's shape allows it; SQL it cannot page (for example with comments) is read whole and previewed only up to that limit.

### Column does not exist on a table

The save check found a column the data source's stored metadata does not list for that table. Check the name, or refresh the data source's metadata if the column was added after the last refresh.

### Names another database

"The SQL names an object in another database" (catalog, project) means a name is qualified with a database other than the one the step's data source is configured for; "has no database configured" means the data source's connection names none. Write the name as `schema.table` (MySQL: `table`), or configure the database on the data source. See [Database names](#database-names).

### Permission Errors

Verify user has SELECT permission:
```sql
-- PostgreSQL
SELECT grantee, table_schema, table_name, privilege_type
FROM information_schema.table_privileges
WHERE grantee = 'your_user';
```

## Related Documentation

- [Subscriptions](/features/subscriptions/) - Schedule query execution
- [Data Sources](/features/data-sources/) - Database connection and project management
- [Data Migration](/features/data-migration/) - Cross-database ETL using the same query layer

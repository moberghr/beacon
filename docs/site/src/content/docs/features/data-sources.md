---
title: Data Sources
description: Configure and manage encrypted database and service connections that Beacon monitors across nine connectors.
---

![Data sources](/img/screenshots/data-sources-light.png)

Data Sources represent database connections that you want to monitor with Beacon.

## Purpose

Data Sources allow you to:
- Connect to multiple databases and services across different servers
- Organize queries by database or application
- Support nine connectors: PostgreSQL, SQL Server, MySQL, Google BigQuery, Snowflake, Databricks, Azure Synapse, AWS CloudWatch, and a generic REST API
- Reuse connections across multiple queries
- Manage credentials securely (connection strings are encrypted at rest with AES-256 via the required `Beacon:EncryptionKey`)

## Use Cases

- **Application Database Monitoring**: Create a data source for each application's database
- **Multi-Database Reporting**: Connect to different databases for consolidated reporting
- **Environment Separation**: Separate data sources for dev, staging, and production
- **Multi-Tenant Monitoring**: One data source per tenant database

## Creating a Data Source

### Step 1: Navigate to Data Sources

1. Log in to Beacon at the React UI (`/login`)
2. Click **Data Sources** in the left navigation menu (`/data-sources`)
3. Click **Create New Data Source**

### Step 2: Fill Data Source Details

| Field | Description | Required | Example |
|-------|-------------|----------|---------|
| **Name** | Descriptive data source name | Yes | `Production Database` |
| **Description** | Purpose of this data source | No | `Main application database monitoring` |
| **Database Type** | Connector engine | Yes | PostgreSQL, SQL Server, MySQL, Google BigQuery, Snowflake, Databricks, Azure Synapse, AWS CloudWatch, or REST API |
| **Connection String** | Connection details | Yes | See examples below |

:::note[Encrypted at rest]
Connection strings are never stored in plaintext. They are encrypted with AES-256 using the required `Beacon:EncryptionKey` and decrypted only when a query runs.
:::

### Step 3: Configure Connection String

**PostgreSQL:**
```
Host=prod-db.company.com;Database=myapp;Username=readonly;Password=secretpass
```

**SQL Server / Azure Synapse:**
```
Server=sql-server.company.com;Database=myapp;User Id=readonly;Password=secretpass;TrustServerCertificate=True
```

**MySQL:**
```
Server=mysql-server.company.com;Database=myapp;Uid=readonly;Pwd=secretpass
```

**Other connectors** (Google BigQuery, Snowflake, Databricks, AWS CloudWatch, and the generic REST API) use connector-specific connection details — for example service-account credentials, account/warehouse identifiers, workspace tokens, AWS region/keys, or a base URL with auth headers. The Create New Data Source form shows the fields required for the connector you select.

### Step 4: Test Connection

1. Click **Test Connection** button
2. Wait for validation (typically 2-5 seconds)
3. Verify success message appears

:::caution
If connection fails, check:
- Database server is accessible from Beacon application
- Credentials are correct
- Database name exists
- User has at least SELECT permissions
:::

### Step 5: Save Data Source

Click **Save** to create the data source.

## Managing Data Sources

### View Data Sources

The Data Sources page shows all configured data sources with:
- Data source name and description
- Database type
- Number of queries using this data source
- Last query execution time
- Actions (Edit, Delete, View Queries)

### Edit Data Source

1. Click **Edit** (pencil icon) on the data source row
2. Modify details or connection string
3. Click **Test Connection** to verify changes
4. Click **Save**

### Delete Data Source

:::caution
Deleting a data source will NOT delete associated queries, but queries will be unable to execute.
:::

1. Click **Delete** (trash icon) on the data source row
2. Confirm deletion in the dialog
3. Data source is archived (soft delete)

## Metadata Loading Options

For database-type data sources, Beacon loads schema metadata (tables, columns, relationships) to power features like the ad-hoc query editor with IntelliSense and AI documentation generation. You can control this behavior when creating or editing a data source.

### Configuration Options

| Option | Description | Default |
|--------|-------------|---------|
| **Metadata Loading Enabled** | Enable/disable automatic metadata loading | Enabled |
| **Max Tables** | Limit the number of tables loaded (0 = unlimited) | 0 |
| **Max Columns Per Table** | Limit columns per table (0 = unlimited) | 0 |
| **Table Names Only** | Load only table names, skip column details | Off |
| **Include Schemas** | Only load metadata from these schemas | All |
| **Exclude Schemas** | Skip metadata from these schemas | None |

### When to Disable Metadata Loading

- **Very large databases** (1000+ tables) where loading metadata is slow
- **Restricted access** databases where the user doesn't have schema read permissions
- **CloudWatch or non-database** data sources (metadata is not applicable)

### When to Use Schema Filters

- **Exclude system schemas** like `information_schema`, `pg_catalog` to reduce noise
- **Include only specific schemas** to focus on relevant tables
- **Multi-schema databases** where you only need metadata from certain schemas

### Viewing Metadata Status

The data source details page shows the metadata loading status in the Overview section:
- **Enabled** (green) - Metadata is loaded and available for IntelliSense
- **Disabled** (red) - Metadata loading is turned off

### Example: Large Database with Schema Filtering

When adding a large production database, limit metadata to relevant schemas:

```
Metadata Loading: Enabled
Max Tables: 200
Include Schemas: public, app
Exclude Schemas: pg_catalog, information_schema, pg_toast
```

This loads only tables from the `public` and `app` schemas, capped at 200 tables.

## Connection Best Practices

### Use Read-Only Users

Create dedicated read-only database users for Beacon:

**PostgreSQL:**
```sql
CREATE USER beacon_readonly WITH PASSWORD 'strong-password';
GRANT CONNECT ON DATABASE your_database TO beacon_readonly;
GRANT USAGE ON SCHEMA public TO beacon_readonly;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO beacon_readonly;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO beacon_readonly;
```

**SQL Server:**
```sql
CREATE LOGIN beacon_readonly WITH PASSWORD = 'strong-password';
USE your_database;
CREATE USER beacon_readonly FOR LOGIN beacon_readonly;
ALTER ROLE db_datareader ADD MEMBER beacon_readonly;
```

**MySQL:**
```sql
CREATE USER 'beacon_readonly'@'%' IDENTIFIED BY 'strong-password';
GRANT SELECT ON your_database.* TO 'beacon_readonly'@'%';
FLUSH PRIVILEGES;
```

### Enable Connection Pooling

For high-frequency queries, enable pooling:

**PostgreSQL:**
```
Host=postgres;Database=db;Username=user;Password=pass;Pooling=true;MinPoolSize=5;MaxPoolSize=20
```

**Benefits:**
- Faster query execution (reuse connections)
- Lower database server load
- Better handling of concurrent subscriptions

### Set Appropriate Timeouts

For long-running queries, increase timeout:

**PostgreSQL:**
```
Host=postgres;Database=db;Username=user;Password=pass;CommandTimeout=300
```

**SQL Server:**
```
Server=sqlserver;Database=db;User Id=user;Password=pass;Connection Timeout=300
```

Match the timeout with subscription timeout setting.

## Examples

### Example 1: Production PostgreSQL

```
Name: Production App Database
Description: Main application database for monitoring
Database Type: PostgreSQL
Connection String: Host=prod-postgres.company.com;Database=appdb;Username=monitor;Password=secret;SSL Mode=Require;Pooling=true;MaxPoolSize=10
```

### Example 2: SQL Server Data Warehouse

```
Name: Data Warehouse
Description: Analytics database for reporting
Database Type: SQL Server
Connection String: Server=dwh.company.com;Database=analytics;User Id=reporting;Password=secret;TrustServerCertificate=True
```

### Example 3: Multi-Tenant MySQL

```
Name: Tenant Database (Customer A)
Description: Customer A's isolated database
Database Type: MySQL
Connection String: Server=mysql.company.com;Database=tenant_a;Uid=readonly;Pwd=secret;SslMode=Required
```

## Troubleshooting

### Connection Test Fails

**Check network connectivity:**
```bash
# Test database host connectivity
ping your-database-host
```

**Verify database is accessible:**
```bash
telnet your-database-host 5432  # PostgreSQL
telnet your-database-host 1433  # SQL Server
telnet your-database-host 3306  # MySQL
```

**Common issues:**
- Firewall blocking connection
- Database not accepting remote connections
- Wrong hostname or port
- VPN required but not connected

### Permission Denied

Ensure database user has necessary grants:

```sql
-- PostgreSQL: Check user permissions
SELECT grantee, privilege_type
FROM information_schema.role_table_grants
WHERE grantee = 'your_user';

-- SQL Server: Check user role
SELECT dp.name AS UserName, dp.type_desc, r.name AS RoleName
FROM sys.database_principals dp
LEFT JOIN sys.database_role_members drm ON dp.principal_id = drm.member_principal_id
LEFT JOIN sys.database_principals r ON drm.role_principal_id = r.principal_id
WHERE dp.name = 'your_user';
```

### SSL Certificate Errors

For self-signed certificates:

**PostgreSQL:**
```
Host=postgres;Database=db;Username=user;Password=pass;SSL Mode=Require;Trust Server Certificate=true
```

**SQL Server:**
```
Server=sqlserver;Database=db;User Id=user;Password=pass;TrustServerCertificate=True
```

## Related Documentation

- [Queries](/features/queries/) - Create queries using this data source
- [Data Migration](/features/data-migration/) - Move data between connected sources
- [Configuration](/getting-started/configuration/) - Connection string reference

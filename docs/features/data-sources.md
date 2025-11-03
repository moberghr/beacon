---
layout: default
title: Data Sources
parent: Features
nav_order: 1
---

# Data Sources

Data Sources represent database connections that you want to monitor with Semantico.

## Purpose

Data Sources allow you to:
- Connect to multiple databases across different servers
- Organize queries by database or application
- Support PostgreSQL, SQL Server, and MySQL
- Reuse connections across multiple queries
- Manage credentials securely

## Use Cases

- **Application Database Monitoring**: Create a data source for each application's database
- **Multi-Database Reporting**: Connect to different databases for consolidated reporting
- **Environment Separation**: Separate data sources for dev, staging, and production
- **Multi-Tenant Monitoring**: One data source per tenant database

## Creating a Data Source

### Step 1: Navigate to Data Sources

1. Log in to Semantico
2. Click **Data Sources** in the left navigation menu
3. Click **Create New Data Source**

### Step 2: Fill Data Source Details

| Field | Description | Required | Example |
|-------|-------------|----------|---------|
| **Name** | Descriptive data source name | Yes | `Production Database` |
| **Description** | Purpose of this data source | No | `Main application database monitoring` |
| **Database Type** | Database engine | Yes | PostgreSQL, SQL Server, or MySQL |
| **Connection String** | Database connection | Yes | See examples below |

### Step 3: Configure Connection String

**PostgreSQL:**
```
Host=prod-db.company.com;Database=myapp;Username=readonly;Password=secretpass
```

**SQL Server:**
```
Server=sql-server.company.com;Database=myapp;User Id=readonly;Password=secretpass;TrustServerCertificate=True
```

**MySQL:**
```
Server=mysql-server.company.com;Database=myapp;Uid=readonly;Pwd=secretpass
```

### Step 4: Test Connection

1. Click **Test Connection** button
2. Wait for validation (typically 2-5 seconds)
3. Verify success message appears

{: .warning }
> If connection fails, check:
> - Database server is accessible from Semantico application
> - Credentials are correct
> - Database name exists
> - User has at least SELECT permissions

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

{: .warning }
> **Careful**: Deleting a data source will NOT delete associated queries, but queries will be unable to execute.

1. Click **Delete** (trash icon) on the data source row
2. Confirm deletion in the dialog
3. Data source is archived (soft delete)

## Connection Best Practices

### Use Read-Only Users

Create dedicated read-only database users for Semantico:

**PostgreSQL:**
```sql
CREATE USER semantico_readonly WITH PASSWORD 'strong-password';
GRANT CONNECT ON DATABASE your_database TO semantico_readonly;
GRANT USAGE ON SCHEMA public TO semantico_readonly;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO semantico_readonly;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO semantico_readonly;
```

**SQL Server:**
```sql
CREATE LOGIN semantico_readonly WITH PASSWORD = 'strong-password';
USE your_database;
CREATE USER semantico_readonly FOR LOGIN semantico_readonly;
ALTER ROLE db_datareader ADD MEMBER semantico_readonly;
```

**MySQL:**
```sql
CREATE USER 'semantico_readonly'@'%' IDENTIFIED BY 'strong-password';
GRANT SELECT ON your_database.* TO 'semantico_readonly'@'%';
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

- [Queries](queries) - Create queries using this data source
- [Configuration](../getting-started/configuration) - Connection string reference
- [Troubleshooting](../troubleshooting/common-issues) - Common connection issues
- [Multi-Tenant Deployments](../advanced/multi-tenant) - Schema-agnostic patterns

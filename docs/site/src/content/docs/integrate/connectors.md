---
title: Data-source connectors
description: Register the connectors for the engines Beacon monitors, what each one supports, how read-only is enforced per engine, and how to create the read-only logins Beacon should use.
---

A **connector** teaches Beacon one kind of data source. Register the ones you need on the `AddBeaconServices(...)` builder, before `UsePostgreSql()` / `UseSqlServer()`:

```csharp
using Beacon.Connector.PostgreSql;
using Beacon.Connector.SqlServer;

builder.Services.AddBeaconServices(builder.Configuration, options => { /* … */ })
    .AddPostgreSqlConnector()
    .AddSqlServerConnector()
    .UsePostgreSql();
```

| Engine | Package | Registration |
|---|---|---|
| PostgreSQL | `Moberg.Beacon.Connector.PostgreSql` | `.AddPostgreSqlConnector()` |
| SQL Server | `Moberg.Beacon.Connector.SqlServer` | `.AddSqlServerConnector()` |
| MySQL | `Moberg.Beacon.Connector.MySql` | `.AddMySqlConnector()` |
| Azure Synapse | `Moberg.Beacon.Connector.AzureSynapse` | `.AddAzureSynapseConnector()` |
| Snowflake | `Moberg.Beacon.Connector.Snowflake` | `.AddSnowflakeConnector()` |
| Google BigQuery | `Moberg.Beacon.Connector.BigQuery` | `.AddBigQueryConnector()` |
| Databricks | `Moberg.Beacon.Connector.Databricks` | `.AddDatabricksConnector()` |
| AWS CloudWatch Logs | `Moberg.Beacon.Connector.CloudWatch` | `.AddCloudWatchConnector()` |
| REST API (OpenAPI) | `Moberg.Beacon.Connector.Api` | `.AddApiConnector()` |

Only registered engines appear in **New data source**. Data sources themselves (connection details, metadata options, project links) are created in the UI by a DataSourceAdmin or an Admin, and their connection strings are encrypted with `Beacon:EncryptionKey` before they are stored. See [Data sources](/features/data-sources/).

## What each engine supports

| Engine | Metadata scan | Database-level read-only | Row cap stops the read early | Migration write target |
|---|---|---|---|---|
| PostgreSQL | Yes | **Yes**: `READ ONLY` transaction, one statement per batch | Yes | Yes |
| MySQL | Yes | **Yes**: `START TRANSACTION READ ONLY`, batches off | Yes | Yes |
| SQL Server | Yes | No: runs in a transaction that is always rolled back | Yes | Yes |
| Azure Synapse | Yes | No | Yes | No |
| Snowflake | Yes | No | Yes | No |
| BigQuery | No | No: the connector's own parser check plus a dry run whose statement type must be `SELECT` | Yes: stops reading the job's result | No |
| Databricks | No | No: the connector's own parser check and configured catalog | Yes: the statement is sent with `row_limit` | No |
| CloudWatch | Log fields | n/a (Logs Insights queries, no SQL) | n/a | No |
| REST API | OpenAPI endpoints | n/a (HTTP requests, no SQL) | n/a | No |

Every SQL engine goes through the same [execution gate](/security/#6-the-sql-execution-gate) before anything runs: length and character checks, a guardrail, a dialect-aware AST validator, the cross-database rule, the refusal of the engine's catalog, statistics and query history, a row cap written into the SQL and a statement timeout. Where the database cannot refuse a write itself, the data source shows a badge and Beacon logs a warning at startup and on save.

:::caution[Always use a read-only login]
The gate is the main defence; database-level read-only is defence in depth. Give every data source a login that **can only read**, on every engine, including PostgreSQL and MySQL. A data-migration write target is the one exception, and it is a separate, Admin-marked data source.
:::

## Creating read-only logins

**PostgreSQL**

```sql
CREATE ROLE beacon_reader LOGIN PASSWORD '…';
GRANT CONNECT ON DATABASE shop TO beacon_reader;
GRANT USAGE ON SCHEMA public TO beacon_reader;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO beacon_reader;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO beacon_reader;
ALTER ROLE beacon_reader SET default_transaction_read_only = on;
```

**SQL Server**

```sql
CREATE LOGIN beacon_reader WITH PASSWORD = '…';
USE shop;
CREATE USER beacon_reader FOR LOGIN beacon_reader;
ALTER ROLE db_datareader ADD MEMBER beacon_reader;
DENY INSERT, UPDATE, DELETE, ALTER, EXECUTE TO beacon_reader;
```

**MySQL**

```sql
CREATE USER 'beacon_reader'@'%' IDENTIFIED BY '…';
GRANT SELECT, SHOW VIEW ON shop.* TO 'beacon_reader'@'%';
```

**Snowflake, Synapse, BigQuery, Databricks:** a role or service principal with read access to the objects Beacon should see, and nothing else (Snowflake: a role with `USAGE` + `SELECT`; BigQuery: `BigQuery Data Viewer` + `BigQuery Job User` on the project; Databricks: `SELECT` + `USE CATALOG`/`USE SCHEMA` through Unity Catalog).

## Connection details per engine

| Engine | The **New data source** dialog asks for |
|---|---|
| PostgreSQL, SQL Server, MySQL, Synapse, Snowflake | Database engine and connection string, plus metadata options: max tables, max columns per table, include and exclude schemas |
| Databricks | Host, HTTP path, personal access token; catalog and schema (optional) |
| BigQuery | Project id, service-account JSON; dataset id and location (optional); `MaximumBytesBilled` and `QueryTimeoutSeconds` in the connection settings (optional) |
| CloudWatch | AWS region, log groups; access key id, secret access key or AWS profile (optional) |
| REST API | Base URL, OpenAPI spec URL, authentication (API key in a header or parameter, bearer token, or basic), include and exclude path patterns |

The last step of the dialog runs **Test connection** before **Create data source**.

### Cost and time limits

- **BigQuery:** every query job is sent with `MaximumBytesBilled`: the data source's own value, or 10 GiB when it sets
  none, lowered by the deployment's `Beacon:DataSources:BigQuery:MaxBytesBilled` when that is set. A job that would bill
  more fails without a charge. Set the per-source value (bytes) as `MaximumBytesBilled` in the connection JSON. A job
  still running when the timeout passes or the caller cancels is cancelled.
- **Databricks:** the statement is submitted without waiting, so a caller's cancellation or the timeout cancels it on the
  warehouse. Results are limited to 16 MiB (a larger one fails) and, on Beacon's capped reads, to the row cap.
- **BigQuery, Databricks:** a result read without a row cap fails above 10,000 rows instead of being cut short.
- **Snowflake:** each statement also runs under the session's `STATEMENT_TIMEOUT_IN_SECONDS`
  (`Beacon:Query:StatementTimeoutSeconds`), reset after the read; a session whose reset fails is closed without
  returning to the driver's pool.
- **BigQuery, Databricks, CloudWatch:** a stored `QueryTimeoutSeconds` above 900 seconds is treated as 900.
- **REST API:** `TimeoutSeconds` is clamped to 1–120 seconds; one response may hold 10 MB; the OpenAPI description is
  fetched within 30 seconds and may hold 50 MB.

See [Warehouse cost limits](/getting-started/configuration/#warehouse-cost-limits).

Queries may only name objects in the database (catalog, project) the data source is configured for: the gate refuses other databases, remote servers, files and dynamic SQL.

## Cross-database queries

A query can have several steps on different data sources. Each step's result is materialised (`@result1`, `@result2`, …) and a final query joins them in an in-memory SQLite database. Nothing is joined across engines at the database level, and the final query passes the same gate. See [Queries](/features/queries/).

## Monitoring your own application's database

To let Beacon (and MCP clients) read your application's own EF Core model (with a table allow-list, column masking and a read-only connection string that is never stored) use `ExposeDbContext<TContext>(...)` instead of creating a data source by hand. See [Extending Beacon](/integrate/extending/#your-dbcontext-as-a-data-source).

## CloudWatch

CloudWatch data sources run Logs Insights queries, not SQL, so they cannot be queried through MCP `query` (the SQL gate
has no dialect for them and refuses the statement). Beacon's own CloudWatch features use the configured log groups
only, so restrict the IAM role to them.

## Notes

- Connector registration also fills process-wide registries (engine → connection factory). Register connectors once, at startup.
- The data-quality SQL generator supports PostgreSQL, SQL Server and MySQL.

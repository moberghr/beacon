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

:::note[Name the database]
Set the database in the connection — `Database` (or `Initial Catalog`) on SQL Server, Azure Synapse, PostgreSQL and MySQL, `db` on Snowflake, the `Catalog` on Databricks, the `ProjectId` on BigQuery. SQL against the data source may name only that database (catalog, project) in a qualifier such as `database.schema.table`; without one configured, no database qualifier is accepted. See [Database names in SQL](/features/authorization/#database-names-in-sql).
:::

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

Pick the **Projects** the data source belongs to, then click **Save** to create it. A new data source that no project links is usable by Admins only until it is linked (see [Projects and access](#projects-and-access)).

Creating, testing a connection for and deleting a data source (`POST /beacon/api/data-sources`, `POST /beacon/api/data-sources/test-connection`, `DELETE /beacon/api/data-sources/{id}`) are a DataSourceAdmin's or an Admin's (see [Functional permissions](/features/authorization/#functional-permissions)). Linking a new data source into a project on create needs that project's admin role, as linking does.

## Managing Data Sources

### View Data Sources

The Data Sources page shows the data sources you can reach with:
- Data source name and description
- Database type
- The projects that link it: the ones you can use by name, plus "+N other" for projects you cannot see (never named). "Admins only" marks a data source no project links yet
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

Archiving evicts the data source's cached metadata and relationship graph, and its metadata is no longer served: reading or refreshing an archived data source's metadata answers `404`. Archiving also clears the column [sample values](#sample-values) stored for the data source (archived tables of it included), so no copy of its rows outlives it; data sources archived before this was in place keep theirs until restored and refreshed. Changing a data source's connection (`IDataSourceService.UpdateDataSource`) evicts both caches as well.

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

### Refreshing Metadata

**Refresh metadata** on a data source (`POST /beacon/api/data-sources/{id}/refresh-metadata`) re-reads its tables and columns from the database now. It changes what every project that links the data source sees, so it needs Write in each of them, or an Admin; a DataSourceAdmin needs Manage only where it acts (see [Projects and access](#projects-and-access)).

- **Cooldown.** A data source's metadata is force-refreshed at most once per `Beacon:Metadata:ForcedRefreshCooldownMinutes` (default `15`; `0` turns the cooldown off; at most `1440`), whoever asks (but an Admin or a DataSourceAdmin, who are not held to it) and from whichever project: the cooldown belongs to the data source, so a refresh asked for in one project linking it also holds back a refresh asked for in another. A second refresh inside the cooldown, or while one is running, answers `429`. The cooldown counts from the last refresh that succeeded, so a failed refresh can be retried at once; it starts over when the data source is changed (for example its connection fixed), and a changed setting applies to the next request. It is kept in memory per Beacon process: on a host running several instances, each instance keeps its own, so the data source can be refreshed once per cooldown on each instance.
- **Curated descriptions are kept.** A table or column description set by applying a documentation patch stays as it is; every other description is taken from the database catalog again (and is cleared when the catalog has none).
- **Names that cannot be quoted safely are left out.** A table or column whose name carries a quote, backtick, bracket, semicolon or control character is not stored, because Beacon builds SQL from catalog names. The refresh answers `skippedNameCount` (the page says how many were left out), and the server log lists their names — control characters written as `\uXXXX`, at most 50 names. A table left out loses its stored description, a curated one included; the applied documentation patch stays in the patch history.
- **Sampling is logged per table.** With sample-value collection on, each table that could not be sampled is logged by name with the reason (refused by the execution gate, failed on the data source, timed out, or a name that is not a plain identifier — never the database's own error text), and every refresh ends with how many of its tables were sampled. Documentation enum discovery logs the same way.
- **Caches.** The refresh replaces the cached metadata, and the relationship graph is rebuilt on its next read.

```json
{
  "Beacon": {
    "Metadata": {
      "ForcedRefreshCooldownMinutes": 15
    }
  }
}
```

### Sample values

A metadata refresh can store a few real values per column (up to five, each cut to 50 characters, or the full set of a
short column's values when it has at most 12) to ground text-to-SQL: they are shown to the model as examples. They are
row data, so:

- **Off by default, and each project's choice.** Sample-value collection (`EnableSampleValueCollection`) is off for a new
  installation. A refresh collects while it is on in the effective settings of every live project that links the data
  source — the project's own override, else the global value — so turning it on for a project is enough even while the
  global value is off, and one linking project that has it off keeps the source unsampled. The global value decides
  alone only for a data source no live project links. The deployment lock `Beacon:Mcp:ForbidRowDataToLlm` turns it off
  everywhere (see [MCP Server → What reaches the LLM provider](/features/mcp-server/#what-reaches-the-llm-provider)).
- **Columns never read.** The refresh reads only the columns whose values may describe them — its sampling statement
  names those columns, and a table with none is not queried. A column is left out when its name is PII (the built-in
  pattern, including the banking identifiers `iban`, `oib` and `jmbg` as a whole name segment — `customer_iban`,
  `ClientOib`, `IBANNumber`, but not `Libanon` or `Kibana` — and account numbers such as `account_number`, or a custom
  PII pattern); names a person's name (`first_name`, `LastName`, `surname`, `customer_name`, Croatian `ime`, `prezime`)
  or Croatian personal data (`adresa`, `telefon`, `mobitel`, `placa`, `stanje`, `broj_racuna`); looks like it holds a
  secret (`password`, `secret`, `token`, `api_key`, `private_key`, `credential`, `cookie`, `passcode`, `passphrase`, a
  connection string, `dsn`, `webhook_url`, and the name segments `pwd`, `session`, `signature`, `pin`, `otp`, `salt`,
  `hash`, `bearer`, `jwt` and `hmac` — so `session_id` and `request_hmac` are left out while `pinned` or `hashtag` are
  not); has a `key` segment beside `api`, `access`, `secret`, `private`, `signing`, `encryption`, `master`, `license`,
  `hmac`, `aes`, `rsa`, `pgp`, `gpg` or `ssh`, or is named just `key` (`hmac_key`, `EncryptionKey` and `ssh_key` are left
  out, while a surrogate key such as `ProductKey` or `ClientKey` is sampled); names a personal financial amount
  (`salary`, `wage`, `balance`, `income` — not a `balance_type` or an `income_category`); or holds binary data.
  Financial amounts stay unmasked in query results.
- **Values that drop a column's samples.** Every value read is screened whole, before it is cut to 50 characters. When
  one looks like PII (an e-mail address, a card or phone number, an IBAN, OIB or JMBG with valid check digits, or a
  custom pattern) or like a credential (a PEM block, a JSON Web Token, a key with a known prefix such as `sk_`, `AKIA`,
  `ghp_`, `xox…-`, `ATATT`, `AIza` or `glpat-`, a bcrypt, argon2 or crypt hash, a run of more than 32 hex digits, a long
  mixed-case base64-like run that is not a slug, SKU or path, a credential written into text such as `password=…`, or
  a URL with a user name and password), the column keeps none. Each refresh logs, as counts only, how many columns it
  left out by name, for a PII-shaped value and for a credential-shaped value.
- **A refresh clears what it no longer samples.** The refresh replaces the stored catalog, so a column that is now left
  out loses the samples it held. Until then, samples stored earlier are screened again whenever they are read — with
  today's name and value rules and the custom patterns of every project that links the source — and withheld if they
  fail.
- **Who sees them.** The REST metadata response (`GET /beacon/api/data-sources/{id}/metadata`, and the refresh's
  response) carries sample values to an Admin only, screened under the settings that apply now; every other caller gets
  the columns without them (the UI does not show them). That Admin-only rule is the REST endpoint's. Grounding reads the
  stored samples itself, under the settings of the asking project and the projects it shares the source with: `ask`
  sends them to the LLM provider, and `get_query_context` returns them to an MCP caller with Read in the project — in
  both cases only while sample-value collection is on for that project and every project sharing the source, and never
  under `Beacon:Mcp:ForbidRowDataToLlm`. Both are recorded in the row-data egress log (event `7123`).
- **Archiving or a migration write target clears them.** Archiving a data source (see
  [Delete Data Source](#delete-data-source)) or marking it as a migration write target clears the samples stored for
  it, the columns of its archived tables included.

### Example: Large Database with Schema Filtering

When adding a large production database, limit metadata to relevant schemas:

```
Metadata Loading: Enabled
Max Tables: 200
Include Schemas: public, app
Exclude Schemas: pg_catalog, information_schema, pg_toast
```

This loads only tables from the `public` and `app` schemas, capped at 200 tables.

## Schema Relationships

Loaded metadata tells Beacon what tables exist; **schema relationships** tell it how to join them. Open a data source and go to **Schema relationships** (`/data-sources/{id}/relationships`) to review and curate them.

- **Foreign key** relationships come from the database's declared constraints and are verified by default.
- **Inferred** relationships are proposed from column naming with a confidence score. Click **Discover** to preview proposals without saving, then accept the ones that are correct.
- **Manual** relationships are declared by hand. One is verified only when its creator may [curate](/features/knowledge-base/#who-curates-grounding) the data source's shared grounding; otherwise it waits, unverified, for a curator to verify it — and a verified relationship that someone else relabels or gives another cardinality goes back to unverified.

Only verified relationships are presented to the SQL generator as authoritative join paths; unverified ones — inferences, and manual relationships pending curator verification — are passed separately and explicitly flagged. The page opens with a **schema health** panel — table and relationship counts, how many are unverified, connected groups, isolated tables, and detected junction tables — which is the fastest way to see which parts of a schema an AI assistant currently has no way to reach.

See the [Knowledge Base guide](/features/knowledge-base/#schema-relationships) for how join paths are used during generation, and the REST endpoints for managing them.

## Projects and access

A data source is reached through the projects that link it ([Project access](/features/authorization/#data-sources-follow-their-projects)): a caller uses it only through a project they may use, and a query that reads several data sources only through one project that links all of them.

- **Linking.** Link data sources on create (the **Projects** field, `projectIds` on `POST /beacon/api/data-sources`) or from a project's page (**Link data sources**, `POST /beacon/api/projects/{id}/data-sources`), which needs that project's admin role or Admin. Anyone but an Admin or a DataSourceAdmin may link only data sources already linked to a live open project; a DataSourceAdmin who is the project's admin links any existing data source, and gains no membership of the projects that already link it. A data source registered by the host for a host project (a [host DbContext](/features/host-dbcontext/) source) links only into the project the host declares for it (the same host key), for Admins too. An unreadable or missing data source answers `404`, a readable one that may not be linked `403`. Each link records who created it.
- **Unlinking.** An Admin unlinks a data source from a project (`DELETE /beacon/api/projects/{id}/data-sources/{dataSourceId}`), or from the **Access** card of a project that shares it, which lists the other projects linking its data sources and their project admins. When that was the source's last link, the source becomes Admin-only until it is linked again; it never becomes unlinked and open. A host's data source cannot be unlinked (`409`), a missing link answers `404`, and a concurrent change `409` (retry).
- **Unlinked data sources.** Data sources that existed before project access stay reachable while no project links them (`openWhenUnlinked`). A new data source is created with `openWhenUnlinked: false`: Admin-only until a project links it. With `Beacon:Authorization:RequireProjectMembership` every unlinked data source is Admin-only.
- **Shared data sources.** A data source linked to an open project is reachable through it by everyone with a role, even when a restricted project links it too. The project page warns Admins about such sources before they restrict a project, and lists every other project, open or restricted, that links them.
- **Counts.** A data source's query count includes only the queries you may read; its migration job count is shown to Admins only (zero for anyone else).
- **Shared state.** Relationships, metadata description edits and a forced metadata refresh change what every linking project sees, so they need Write in every live project that links the data source, or an Admin; a DataSourceAdmin needs Write (or Manage) only in a project it acts through. Curating that state — verifying a relationship, deleting a verified one, applying or rejecting a documentation patch that writes a description — needs the project admin role in every live project that links the data source, the DataSourceAdmin permission, or an Admin (see [Who curates grounding](/features/knowledge-base/#who-curates-grounding)).
- **The configured database matters.** SQL Beacon runs against the data source may name only the database its connection is configured for (the catalog on Databricks, the project on BigQuery); a name such as `otherdb.dbo.orders` is refused, and a data source without a configured database accepts no database qualifier. Changing the connection's database changes what its saved SQL may name. See [Database names in SQL](/features/authorization/#database-names-in-sql).
- **Shared guardrails.** A data source several live projects link runs MCP SQL, and is grounded and sampled, with the strictest guardrails among those projects (read-only enforcement, PII detection and patterns, row, timeout, size and plan-cost limits, value grounding and its probe budget, sample-value collection); an Admin sees them per source on the project page. See [Guardrails of shared data sources](/features/authorization/#guardrails-of-shared-data-sources).
- **One database login per boundary.** Project access decides who may run SQL through Beacon, not what the login can read: use a login per data source that sees only what its projects should see, rather than one broad login shared by several boundaries. A login that can read several boundaries defeats project separation — Beacon refuses names in another database, but a view, synonym or function in the source's own database still reads whatever the login may.

## Read-only guarantees per engine

Every statement Beacon runs on a data source first passes its SQL checks (a regex guardrail, a read-only parser check in
the engine's dialect, the configured-database rule and a row cap) and then runs on the provider's read-only path. What
that path adds at the database differs by engine, and on every engine it is defence in depth, not a replacement for the
data source's own permissions: **connect every data source Beacon reads with a login that can only read.**

| Engine | What the database itself does on Beacon's read-only path | Data-source badge |
|---|---|---|
| PostgreSQL | Session `default_transaction_read_only = on` plus a `READ ONLY` transaction, and the statement is sent as a batch of exactly one command, so the driver and the server refuse a second statement (one that would end the transaction and open a writable one): the server refuses any write. | Enforced by the engine — still use a read-only login |
| MySQL / MariaDB | `SET SESSION TRANSACTION READ ONLY` plus `START TRANSACTION READ ONLY` on a connection without statement batches, rolled back afterwards: the server refuses any write. | Enforced by the engine — still use a read-only login |
| SQL Server | No read-only session or transaction mode exists. The statement runs inside a transaction that is always rolled back, so a write the SQL checks missed is undone rather than committed — but it is not refused, and work outside the transaction (a sequence value, a call to another server) is not undone. A statement that ends that transaction itself (a `COMMIT` or `ROLLBACK` in the batch) fails the run: its result is discarded and the security log records `ReadOnlyTransactionEnded` (7116, the data source id) — what it did after that point was not undone. | **Read-only login required** |
| Azure Synapse | No read-only mode; dedicated and serverless pools differ in what transactions they allow, so the statement runs plainly. | **Read-only login required** |
| Snowflake | No read-only session (DDL commits on its own); the statement runs plainly. | **Read-only login required** |
| BigQuery | No read-only session; the connector also requires a dry run to report statement type `SELECT`. | **Read-only login required** |
| Databricks | No read-only session; the connector re-checks the SQL with its own parser and the configured catalog. | **Read-only login required** |

On every engine without a database-level guarantee, Beacon's SQL checks are the only thing between a statement and the
data source's own permissions, so a **login (role, service account, token) that can only read** is the boundary. The data
source list and detail pages mark such sources, and Beacon logs a warning (naming the data source by id only) when one is
saved and when the host starts — except a [migration write target](#migration-write-targets), which Beacon never reads and
labels as one instead. The `databaseReadOnlyEnforced` field of `GET /beacon/api/data-sources` and
`GET /beacon/api/data-sources/{id}` reports it: `true` (the engine refuses writes), `false` (it does not — use a read-only
login) or `null` (the source runs no SQL, such as CloudWatch or an API source).

A SQL Server query over memory-optimized tables runs inside that rolled-back transaction, which such tables accept only
under snapshot isolation: if it fails with error 41368, turn on `MEMORY_OPTIMIZED_ELEVATE_TO_SNAPSHOT` for the database.

The column sample values collected during a metadata refresh are read on the same read-only path.

## Migration write targets

The one exception to read-only logins is a **migration write target**: a data source an Admin marked as the destination
of [data migrations](/features/data-migration/#migration-write-targets) (data source page → **Migration writes**, or
`PUT /beacon/api/data-sources/{id}/migration-writes` with `{ "allowMigrationWrites": true }`; Admin only, and only while
the host runs migrations, `Beacon:Migrations:Enabled`). Beacon never reads a write target — query steps, previews,
subscriptions, data quality, MCP tools, AI grounding, documentation and metadata scans refuse it or leave it out, and its
catalog is not offered — so its login may hold the grants a migration needs to write
([destination privileges](/features/data-migration/#destination-privileges)). The data source list and page label it
**Migration write target** instead of asking for a read-only login, and `allowMigrationWrites` in
`GET /beacon/api/data-sources` and `GET /beacon/api/data-sources/{id}` reports it; `?migrationTargets=true` lists only
write targets, `false` only the sources Beacon reads.

- Only a PostgreSQL, MySQL or SQL Server database can be marked, and not a host-managed data source or one marked
  read-only.
- A data source that live queries, subscriptions or data contracts read is marked only when the Admin confirms it
  (`"force": true`): otherwise the request is refused with `409` naming how many of each read it, as they fail once
  Beacon stops reading the source. The page shows that reason and asks a second time.
- Marking and unmarking are recorded in the settings history (`DataSource.{id}.AllowMigrationWrites`, before and after,
  and who) and in the security log (`MigrationWritesChanged`, ids only).
- Marking a data source clears the column sample values Beacon collected through its login, so stored metadata never
  shows values read before it became a write target.
- Before you return a write target to a read source, change its login back to one that can only read: from then on
  Beacon reads it again. The page asks for confirmation and says so.
- Every other data source — including the source of a migration — should keep a login that can only read.

## Connection Best Practices

### Use Read-Only Users

Create dedicated read-only database users for every data source Beacon reads (every data source except a
[migration write target](#migration-write-targets)). On SQL Server, Azure Synapse, Snowflake, BigQuery and Databricks
this is the only database-side guarantee that Beacon cannot write (see
[Read-only guarantees per engine](#read-only-guarantees-per-engine)):

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

Beacon refuses linked-server (four-part) names, and a three-part `database.schema.table` name only when it names the
data source's own configured database. A synonym or view can still reach other databases the login can see, so don't map
the Beacon login on any linked server, don't grant it `ALTER ANY LINKED SERVER`, and give it access only to the
databases Beacon should read.

**MySQL:**
```sql
CREATE USER 'beacon_readonly'@'%' IDENTIFIED BY 'strong-password';
GRANT SELECT ON your_database.* TO 'beacon_readonly'@'%';
FLUSH PRIVILEGES;
```

**Snowflake:** a role with `USAGE` on the warehouse, database and schemas and `SELECT` on the tables (no `OWNERSHIP`,
`INSERT`, `UPDATE`, `CREATE` or `OPERATE`), set as the user's default role or as `role=` in the connection.

**BigQuery:** a service account with `roles/bigquery.dataViewer` on the datasets and `roles/bigquery.jobUser` on the
project — not `dataEditor` or `admin`.

**Databricks:** a token for a principal with `USE CATALOG`, `USE SCHEMA` and `SELECT` only, and `CAN USE` on the SQL
warehouse.

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
- [Knowledge Base & Grounding](/features/knowledge-base/) - Join paths, glossary, and the grounding context built from this metadata
- [Configuration](/getting-started/configuration/) - Connection string reference

---
title: Data Sources
description: "Connect the databases and services Beacon reads: nine connectors, credentials encrypted at rest, access through projects, and per-engine read-only guarantees."
---

![Data sources](/img/screenshots/data-sources-light.png)

A data source is one connection Beacon reads: a database, a warehouse, a CloudWatch log set or an HTTP API. Queries,
subscriptions, data contracts, MCP tools and the AI layer all reach data through data sources, and data sources are
reached through the [projects](#projects-and-access) that link them.

## Connectors

| Kind in the UI | Engine | What Beacon reads |
|---|---|---|
| **Database** | PostgreSQL, SQL Server, MySQL, Azure Synapse, Snowflake | SQL, plus tables and columns for metadata |
| **AWS CloudWatch** | n/a | Log groups; metadata discovers the log fields |
| **Databricks** | n/a | SQL against a SQL warehouse |
| **BigQuery** | n/a | SQL against a project |
| **API (OpenAPI)** | n/a | An HTTP API described by an OpenAPI spec; metadata imports its endpoints |

Each connector is a separate package the host registers (see [Data-source connectors](/integrate/connectors/)).

Connection details are encrypted at rest with AES-256 under the required `Beacon:EncryptionKey` and decrypted only when
Beacon connects (see [Configuration](/getting-started/configuration/#encryption-key-configuration-required)).

## Create a data source

Creating, testing a connection for and deleting a data source are a **DataSourceAdmin**'s (an Admin holds the
permission). See [Functional permissions](/security/authorization/#functional-permissions).

1. Open **Data Sources** (`/beacon/data-sources`) and click **New data source**.
2. **Type.** Pick the **Data source type**.
3. **Connection.** Enter a **Name** and the connection details for that type (below).
4. **Test & save.** Review the summary, pick the **Projects** to link the data source into, click **Test connection**
   (it shows **Connected** or **Failed** with the reason), then **Create data source**.

Without a project, the new data source is usable by Admins only until a project links it. Picking projects needs a
Viewer or Editor role, and linking into a project needs that project's admin role, as [linking](#projects-and-access)
does.

### Connection fields

| Type | Fields (required in bold) |
|---|---|
| Database | **Database engine**, **Connection string**, and the [metadata options](#metadata-loading-options) |
| AWS CloudWatch | **AWS region**, **Log groups (comma-separated)**, Access key id, Secret access key, AWS profile |
| Databricks | **Host**, **HTTP path**, **Personal access token**, Catalog, Schema |
| BigQuery | **Project id**, Dataset id, Location, **Service account JSON** |
| API (OpenAPI) | **Base URL**, **OpenAPI spec URL**, Authentication (None, API key, Bearer token, Basic auth), Include path patterns, Exclude path patterns |

Connection string examples for the database engines:

```text
# PostgreSQL
Host=prod-db.example.com;Database=appdb;Username=beacon_readonly;Password=...;SSL Mode=VerifyFull

# SQL Server / Azure Synapse
Server=sql.example.com;Database=appdb;User Id=beacon_readonly;Password=...;Encrypt=Strict

# MySQL
Server=mysql.example.com;Database=appdb;Uid=beacon_readonly;Pwd=...;SslMode=VerifyFull

# Snowflake
account=myorg-myaccount;user=beacon_readonly;password=...;db=APPDB;warehouse=BEACON_WH
```

Each example authenticates the server over TLS, which Beacon requires: see [Transport security (TLS)](#transport-security-tls).

:::note[Name the database]
Set the database in the connection: `Database` (or `Initial Catalog`) on SQL Server, Azure Synapse, PostgreSQL and
MySQL, `db` on Snowflake, the **Catalog** on Databricks, the **Project id** on BigQuery. SQL against the data source may
name only that database (catalog, project) in a qualifier such as `database.schema.table`; without one configured, no
database qualifier is accepted. See [Database names in SQL](/security/authorization/#database-names-in-sql).
:::

If **Test connection** fails, check that the host can reach the server (firewall, VPN, private endpoint), that the
credentials and database name are right, and that the login can read.

### Transport security (TLS)

Every data-source connection must encrypt its traffic and authenticate the server: the server's certificate must chain
to a trusted certificate authority and name the host Beacon connects to. Otherwise anyone on the network path can
read the credentials and the rows. Each connector checks its connection with its driver's own parser, so option
synonyms and case do not matter.

| Engine | Required | Server certificate from a private CA |
|---|---|---|
| PostgreSQL | `SSL Mode=VerifyFull`. `Require` and `VerifyCA` do not check the host name, and the default (`Prefer`) may not encrypt at all. | `Root Certificate=/path/to/ca.pem` |
| SQL Server, Azure Synapse | `Encrypt` set to `Mandatory` (`True`, the default) or `Strict`, and `TrustServerCertificate` left out or `false`. | Install the CA on the Beacon host, or pin it: `HostNameInCertificate=<name in the certificate>` when it differs from the server name, `ServerCertificate=/path/to/server.cer` to accept exactly that certificate. |
| MySQL | `SslMode=VerifyFull`. `Required` and `VerifyCA` do not check the host name, and the default (`Preferred`) may not encrypt at all. | `SslCa=/path/to/ca.pem` |
| Snowflake | Nothing to set. `insecureMode=true`, a `certRevocationCheckMode` other than `ENABLED` and a `scheme` other than `https` are refused. | Not applicable |
| API (OpenAPI) | **Base URL** and **OpenAPI spec URL** are absolute `https://` URLs. | Install the CA on the Beacon host. |
| Databricks, BigQuery, AWS CloudWatch | Nothing to set: their clients always connect over HTTPS and check the certificate. | Not applicable |

A CA file is read on the Beacon host, so it must exist there. A connection Beacon cannot read, or whose type has no
connector registered, is never verified.

How Beacon applies the rule:

- **Creating a data source, changing its connection, and Test connection** refuse a connection that does not meet it.
  The message names the option to change, never a value from the connection string.
- **A data source stored before the rule** keeps working, so an upgrade does not stop running queries and
  subscriptions. The data-source list reports it (`transportVerified: false`), and the log carries one Warning per data
  source per process, naming it by id. Replace its connection with one that meets the rule. A host that sets
  `Beacon:DataSources:Transport:EnforceOnExisting` to `true` refuses such a source every time it connects.
- **Host-managed data sources** follow the same rules, applied to the connection string the host configures, which is
  read each time the source connects. That string is never refused as a new connection, since no Admin enters it in
  Beacon: it is checked each time the source connects, with the Warning (or, under `EnforceOnExisting`, the refusal)
  of a stored source. A host that changes the string to one that does not meet the rule therefore gets the Warning,
  not a refusal, unless it sets `EnforceOnExisting`.
- **A migration write target** has no badge: Beacon never decrypts its connection to show one. The migration writer
  applies the same rule each time it connects to it.
- **`Beacon:DataSources:Transport:AllowInsecure`** lifts the rule for development hosts whose local databases have no
  TLS (the sample host sets it in its Development configuration). Outside Development the host logs a Warning at
  startup while it is set. See [Configuration](/getting-started/configuration/#data-source-transport-security).

### Engine check

The **Database engine** you pick decides the driver, the SQL grammar and the read-only checks Beacon applies, so
Beacon asks the server which engine it is when you **create** a database data source, **change its connection or
engine**, and click **Test connection**. It runs one fixed catalog statement, outside the SQL gate, like the metadata
scan's catalog SQL:

| Declared engine | Statement | Refused when the server is |
|---|---|---|
| PostgreSQL | `SELECT version()` | not PostgreSQL: the answer does not start with `PostgreSQL`, or names Amazon Redshift, Greenplum, CockroachDB or YugabyteDB |
| SQL Server | `SELECT CAST(SERVERPROPERTY('EngineEdition') AS int)` | Azure Synapse (edition 6 or 11) |
| Azure Synapse | the same | SQL Server (any other edition) |
| MySQL | `SELECT VERSION()` | never: MariaDB and TiDB pass with a note in the test result and a Warning in the log |

Snowflake, BigQuery, Databricks, CloudWatch and API sources are not checked.

- **A clear mismatch** refuses the create or the change (`400`) and fails **Test connection**, with a message naming
  the declared and the detected engine, never a version string or a host name. Nothing is saved.
- **A server that cannot be reached, or whose answer cannot be read**, never blocks the save: the log carries a
  Warning naming the data source by id. **Test connection** reports reachability as before; when the server answered
  the connection but not the engine question, it says *Connection successful. The server's engine could not be
  verified.*
- **An edit that keeps the stored connection and engine** connects to nothing.
- **A disabled data source** is not checked (it makes no connection, also when its credentials are rotated): the check
  runs the next time its connection or engine changes while it is enabled.
- A refused connection's pooled connections are emptied, so none opened with the refused credentials is reused.
- **Under the approval workflow** (`CoversDataSourceConnections`), a source created or changed in anything but its
  secrets connects to nothing until approved, so neither the create nor the change runs the check: it runs when an
  Approver approves the connection. Then a clear mismatch, and a server that cannot be reached or whose engine cannot be
  read, refuse the approval (`409`) and the request stays pending; a disabled source is enabled first (`409`). A change
  of the secrets alone is checked when it is saved, as without the workflow. See
  [Approvals](/security/approvals/).

## Manage data sources

The **Data Sources** list is paged on the server (20 rows per page, at most 200) and can be searched by name. Its
columns are **Id**, **Name**, **Type**, **Engine** (with the read-only badge), **Projects**, **Queries** and
**Migrations**. A user who holds DataSourceAdmin but no Viewer or Editor role can add data sources but cannot list
them.

- **Projects** shows the projects that link the data source and that you may use, plus "+N other" for projects you
  cannot see (never named). "Admins only" marks a data source no project links.
- **Queries** counts only the queries you may read; the migration job count is shown to Admins only.

Click a row to open the data source page: type, table, column and query counts, the read-only and migration-write
status, **Metadata loading**, the linked projects, and the schema browser. Its actions are **Relationships**,
**Refresh metadata** and **Delete**.

### Edit a data source and replace its connection

`PUT /beacon/api/data-sources/{id}` (DataSourceAdmin or Admin) changes a data source's name, engine, metadata loading
options and connection. It is how credentials are rotated: send the new connection string, and the next connection uses
it. The stored connection is never shown, so it is never sent back: a body whose `connectionString` is `null` or empty
keeps the stored connection and changes only the other fields. A stored connection Beacon cannot decrypt (encrypted under
a key no longer configured) cannot be kept; enter it again. Beacon logs that it could not decrypt it, with the data source
id and engine only.

`name` is required. A database data source needs `databaseEngineType`, and changing the engine needs the connection
entered again: a stored connection is never read as another engine's (`400`). The metadata options are optional: one
that is left out or `null` keeps the stored value, and an empty schema list clears the stored one.

```json
{
  "name": "warehouse",
  "databaseEngineType": "PostgreSQL",
  "connectionString": "Host=db.example.com;Database=sales;Username=beacon_reader;Password=...;SSL Mode=VerifyFull",
  "metadataLoadingEnabled": true,
  "metadataMaxTables": 0,
  "metadataMaxColumnsPerTable": 0,
  "metadataLoadTableNamesOnly": false,
  "metadataExcludeSchemas": [],
  "metadataIncludeSchemas": [],
  "confirmTargetChange": false
}
```

- **Transport.** A new connection, or the stored one read as another engine's, must authenticate the server over TLS
  (see [Transport security](#transport-security-tls)); otherwise the request answers `400` naming the option to change.
- **Engine.** A new connection or engine is checked against the server (see [Engine check](#engine-check)): a server of
  another engine than the declared one answers `400` and nothing changes.
- **Change of target.** A connection to another engine, host, port or database than the stored one, as the engine's
  transport check reads them, is saved only when the body sets `confirmTargetChange: true`; otherwise the request
  answers `409` and nothing changes. A connection whose host, port or database Beacon cannot read (the JSON-configured
  connectors without a host) counts as a change of target. The host name is compared without regard to case, the
  database name exactly. A confirmed change removes the column [sample values](#sample-values) stored for the source, in
  the same save, because they were read from the old target.
- **Schema after a change of target.** The stored tables and columns are not deleted: a confirmed change of target
  enqueues a background metadata refresh (when the source loads metadata) that replaces them with the new target's,
  keeping the curated descriptions of the tables and columns that still exist. Until it finishes, the schema browser and
  AI grounding show the old target's tables. A host whose job runner has no metadata refresh job logs that none was
  enqueued; run **Refresh metadata** then.
- **Old connections.** After a connection change, Beacon empties the driver's connection pools for the old connection
  string on PostgreSQL, MySQL (the read-only pool, which connects without statement batches, included), SQL Server and
  Azure Synapse, so no idle connection opened with the old credentials is reused. A connection already in use finishes
  its work and is closed when it is returned. Snowflake and the JSON-configured connectors (Databricks, BigQuery,
  CloudWatch, API) keep no pool Beacon can empty; for a database engine without one, Beacon logs that no pool was
  cleared. A pool that cannot be cleared does not undo the saved change; the failure is logged by its type.
- **Caches.** Every edit evicts the cached metadata and relationship graph and restarts the forced-refresh cooldown.
- **Audit.** A change of connection is recorded as `data_sources.credentials_rotated`, any other edit as
  `data_sources.data_source_updated`; each names the fields that changed and whether the target changed, never a value
  (see [Security audit trail](/security/audit-trail/)).

A data source the host registered (a [host DbContext](/mcp/host-dbcontext/) source) cannot be edited here
(`400`): the host rotates its credentials in its own configuration. A rotation takes effect at once; it needs no second
person's approval. Under the approval workflow (`CoversDataSourceConnections`) that holds for a change of the secrets
alone (the password, a key); a change of any other setting of the connection (host, port, database, login, options) is
saved and the source connects to nothing until a second person approves it.

- **Who may point a source elsewhere.** A DataSourceAdmin may point a data source at another server or database, as one
  may create a data source there. The change must be confirmed and is recorded with `targetChanged: true`, so the audit
  trail shows every such change; a second person's approval of a target change is not required unless the approval
  workflow covers data-source connections.
- **Concurrent edits.** Two edits of the same data source saved at the same time are not merged: the one saved last is
  kept. Queries already running when the connection changes finish on the connection they opened.

### Disable and enable a data source

`PUT /beacon/api/data-sources/{id}/enabled` with `{ "enabled": false }` (DataSourceAdmin or Admin) disables a data
source at once; `{ "enabled": true }` enables it again. Disabling is the emergency stop for a source whose credentials
may be compromised: it takes effect immediately and needs no second person's approval.

A disabled source makes no connection at all. The connection resolver, the one place a source's stored connection data
is decrypted, refuses it first, before anything is decrypted or its transport checked. So, while it is disabled:

- queries, previews, ad-hoc queries, data-quality checks, AI and MCP paths and metadata scans that read it fail with
  the message "This data source is disabled" (`400`); a data-quality rule result says the same;
- a subscription run whose query reads it fails before any step runs, and is recorded in the run history as failed
  with the reason "Failed: a data source this query reads is disabled." (no task, no delivery); a run someone starts
  answers `409`;
- the project listings that MCP tools and AI grounding read leave it out;
- its stored catalog is not served: a metadata read answers an empty catalog (as for a migration write target), and its
  cached metadata and relationship graph are evicted when it is disabled or enabled, so none of its stored tables,
  columns or sample values is shown while it is disabled;
- the driver's connection pool for its connection is emptied when it is disabled.

The data source keeps its queries, subscriptions, projects and metadata, and all of them work again once it is enabled.
The list and detail responses carry `disabled` and `disabledTime`. Who disabled it and when are stored on the source;
each change is recorded as `data_sources.data_source_disabled` or `data_sources.data_source_enabled`, with the data
source id only. Setting the state a source already has changes and records nothing. A host-registered data source
cannot be disabled here (`400`).

### Delete a data source

**Delete** (`DELETE /beacon/api/data-sources/{id}`) archives the data source (soft delete). It is refused while a live
query or migration job still uses it, the message names them, and a data source the host registered (a
[host DbContext](/mcp/host-dbcontext/) source) cannot be deleted.

Archiving evicts the cached metadata and relationship graph; reading or refreshing an archived data source's metadata
answers `404`. It also clears the column [sample values](#sample-values) stored for it (archived tables included), so
no copy of its rows outlives it.

## Metadata loading options

For database data sources Beacon loads tables, columns and relationships. They drive the schema browser, the SQL
editor's explorer, the save-time column check, documentation and AI grounding. The options are set when the data source
is created:

| Option | Default | What it does |
|---|---|---|
| **Enable metadata loading** | On | Load metadata at all |
| **Load table names only** | Off | Skip column details |
| **Max tables** | `0` (unlimited) | Cap the number of tables loaded |
| **Max columns / table** | `0` (unlimited) | Cap the columns loaded per table |
| **Exclude schemas** | none | Comma-separated schemas to skip, for example `information_schema, pg_catalog` |
| **Include schemas** | all | Comma-separated schemas to load, for example `public, app` |

Turn loading off, or narrow it with schema filters, for very large databases (thousands of tables) and for logins that
cannot read the catalog.

### Refreshing metadata

**Refresh metadata** on a data source re-reads its tables and columns from the database. It changes what every project
that links the data source sees, so it needs Write in each of them, or an Admin; a DataSourceAdmin needs Manage only
where it acts (see [Projects and access](#projects-and-access)). A [migration write target](#migration-write-targets) has
no refresh, because Beacon never reads it. There are two ways to start one:

- **As a background task** (what the page's button does): `POST /beacon/api/data-sources/{id}/metadata-refreshes`
  answers `202` at once with `{ taskId, jobId, state }` and a `Location` header. Read its progress with
  `GET /beacon/api/data-sources/{id}/metadata-refreshes/{taskId}` (`state`: `0` queued, `1` running, `2` succeeded,
  `3` failed, `4` skipped; `failureCode` says why a failed or skipped one did not finish, `warningCount` is the number
  of names left out), with the same access check. The job runs as the user who started it and checks again, right
  before the scan, that they may still manage the data source: if not, it is skipped (`forbidden`). A refresh that
  finds another refresh of the source running in the same Beacon process is skipped too (`busy`; that refresh's result
  is the current catalog). A signed-in user who started it also gets `JobStatusChanged` events for `jobId` on the
  SignalR hub (an API key does not). One refresh of a data source is queued or running at a time; one queued or
  running for more than an hour, or still queued without a job after five minutes, is given up (`abandoned`) when the
  next one starts or its progress is read. The job runs through your host's
  [`IBeaconScheduler.EnqueueBackgroundTask`](/integrate/background-jobs/); a host that does not implement it runs the
  refresh in the request, and the `202` then carries the state it finished in.
- **In the request** (kept for API callers of Beacon 5.0): `POST /beacon/api/data-sources/{id}/refresh-metadata`
  answers with the refreshed catalog once the scan is done, which holds the request open for as long as the scan takes.

- **Cooldown.** A data source's metadata is force-refreshed at most once per
  `Beacon:Metadata:ForcedRefreshCooldownMinutes` (default `15`; `0` turns the cooldown off; at most `1440`), whoever asks
  (Admins and DataSourceAdmins are not held to it) and from whichever project: the cooldown belongs to the data source.
  A second refresh inside the cooldown, or while one is running, answers `429`. The cooldown counts from the last refresh
  that succeeded, so a failed refresh can be retried at once. It is kept in memory per Beacon process (for a background
  refresh, the process whose job ran it): on a host running several instances, each instance keeps its own. A background
  refresh queued or running anywhere refuses another start on every instance.
- **Curated descriptions are kept.** A table or column description set by applying a documentation patch stays; every
  other description is taken from the database catalog again (and cleared when the catalog has none).
- **Names that cannot be quoted safely are left out.** A table or column whose name carries a quote, backtick, bracket,
  semicolon or control character is not stored, because Beacon builds SQL from catalog names. The refresh answers
  `skippedNameCount` (a background refresh: `warningCount`; the page says how many were left out), and the server log lists their names: control characters
  written as `\uXXXX`, at most 50 names. A table left out loses its stored description, a curated one included.
- **Sampling is logged per table.** With sample-value collection on, each table that could not be sampled is logged by
  name with the reason (refused by the execution gate, failed on the data source, timed out, or a name that is not a
  plain identifier, never the database's own error text), and every refresh ends with how many tables were sampled.
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
  source (the project's own override, else the global value) so turning it on for a project is enough even while the
  global value is off, and one linking project that has it off keeps the source unsampled. The global value decides
  alone only for a data source no live project links. The deployment lock `Beacon:Mcp:ForbidRowDataToLlm` turns it off
  everywhere (see [MCP Server → What reaches the LLM provider](/mcp/server/#what-reaches-the-llm-provider)).
- **Columns never read.** The refresh reads only the columns whose values may describe them; its sampling statement
  names those columns, and a table with none is not queried. A column is left out when its name is PII (the built-in
  pattern, including the banking identifiers `iban`, `oib` and `jmbg` as a whole name segment: `customer_iban`,
  `ClientOib`, `IBANNumber`, but not `Libanon` or `Kibana`, and account numbers such as `account_number`, or a custom
  PII pattern); names a person's name (`first_name`, `LastName`, `surname`, `customer_name`, Croatian `ime`, `prezime`)
  or Croatian personal data (`adresa`, `telefon`, `mobitel`, `placa`, `stanje`, `broj_racuna`); looks like it holds a
  secret (`password`, `secret`, `token`, `api_key`, `private_key`, `credential`, `cookie`, `passcode`, `passphrase`, a
  connection string, `dsn`, `webhook_url`, and the name segments `pwd`, `session`, `signature`, `pin`, `otp`, `salt`,
  `hash`, `bearer`, `jwt` and `hmac`, so `session_id` and `request_hmac` are left out while `pinned` or `hashtag` are
  not); has a `key` segment beside `api`, `access`, `secret`, `private`, `signing`, `encryption`, `master`, `license`,
  `hmac`, `aes`, `rsa`, `pgp`, `gpg` or `ssh`, or is named just `key` (`hmac_key`, `EncryptionKey` and `ssh_key` are left
  out, while a surrogate key such as `ProductKey` or `ClientKey` is sampled); names a personal financial amount
  (`salary`, `wage`, `balance`, `income`; not a `balance_type` or an `income_category`); or holds binary data.
  Financial amounts stay unmasked in query results.
- **Values that drop a column's samples.** Every value read is screened whole, before it is cut to 50 characters. When
  one looks like PII (an e-mail address, a card or phone number, an IBAN, OIB or JMBG with valid check digits, or a
  custom pattern) or like a credential (a PEM block, a JSON Web Token, a key with a known prefix such as `sk_`, `AKIA`,
  `ghp_`, `xox…-`, `ATATT`, `AIza` or `glpat-`, a bcrypt, argon2 or crypt hash, a run of more than 32 hex digits, a long
  mixed-case base64-like run that is not a slug, SKU or path, a credential written into text such as `password=…`, or
  a URL with a user name and password), the column keeps none. Each refresh logs, as counts only, how many columns it
  left out by name, for a PII-shaped value and for a credential-shaped value.
- **A refresh clears what it no longer samples.** The refresh replaces the stored catalog, so a column that is now left
  out loses the samples it held. Until then, samples stored earlier are screened again whenever they are read (with
  today's name and value rules and the custom patterns of every project that links the source) and withheld if they
  fail.
- **Who sees them.** The REST metadata response (`GET /beacon/api/data-sources/{id}/metadata`, and the refresh's
  response) carries sample values to an Admin only, screened under the settings that apply now; every other caller gets
  the columns without them (the UI does not show them). Grounding reads the stored samples itself, under the settings of
  the asking project and the projects it shares the source with: `ask` sends them to the LLM provider, and
  `get_query_context` returns them to an MCP caller with Read in the project; in both cases only while sample-value
  collection is on for that project and every project sharing the source, and never under
  `Beacon:Mcp:ForbidRowDataToLlm`. Both are recorded in the row-data egress log (event `7123`).
- **Archiving or a migration write target clears them.** Archiving a data source (see
  [Delete a data source](#delete-a-data-source)) or marking it as a migration write target clears the samples stored for
  it, the columns of its archived tables included.

## Schema relationships

Loaded metadata tells Beacon what tables exist; **schema relationships** tell it how to join them. Open a data source
and click **Relationships** (`/beacon/data-sources/{id}/relationships`) to review and curate them.

- **Foreign key** relationships come from the database's declared constraints and are verified by default: one per
  column pair of every constraint, so a column in several foreign keys (on its own and inside a composite key) gets one
  for each.
- **Inferred** relationships are proposed from column naming with a confidence score. Click **Discover** to preview
  proposals without saving, then accept the ones that are correct.
- **Manual** relationships are declared by hand. One is verified only when its creator may
  [curate](/mcp/knowledge-base/#who-curates-grounding) the data source's shared grounding; otherwise it waits,
  unverified, for a curator, and a verified relationship that someone else relabels or gives another cardinality goes
  back to unverified.

Only verified relationships are presented to the SQL generator as authoritative join paths; unverified ones are passed
separately and flagged. The page opens with a **schema health** panel (table and relationship counts, how many are
unverified, connected groups, isolated tables and detected junction tables) which shows which parts of a schema an AI
assistant cannot reach. See the [Knowledge Base guide](/mcp/knowledge-base/#schema-relationships).

## Projects and access

A data source is reached through the projects that link it
([Project access](/security/authorization/#data-sources-follow-their-projects)): a caller uses it only through a project
they may use, and a query that reads several data sources only through one project that links all of them. Lists are
filtered in the database by the same rule.

- **Linking.** Link data sources on create (the **Projects** field, `projectIds` on `POST /beacon/api/data-sources`) or
  from a project's page (**Link data sources**, `POST /beacon/api/projects/{id}/data-sources`), which needs that
  project's admin role or Admin. Anyone but an Admin or a DataSourceAdmin may link only data sources already linked to a
  live open project; a DataSourceAdmin who is the project's admin links any existing data source, and gains no
  membership of the projects that already link it. A data source registered by the host for a host project (a
  [host DbContext](/mcp/host-dbcontext/) source) links only into the project the host declares for it, for Admins
  too. An unreadable or missing data source answers `404`, a readable one that may not be linked `403`. Each link
  records who created it.
- **Unlinking.** An Admin unlinks a data source from a project
  (`DELETE /beacon/api/projects/{id}/data-sources/{dataSourceId}`), or from the **Access** card of a project that shares
  it. When that was the source's last link, the source becomes Admin-only until it is linked again; it never becomes
  unlinked and open. A host's data source cannot be unlinked (`409`), a missing link answers `404`, and a concurrent
  change `409` (retry).
- **Unlinked data sources.** Data sources that existed before project access stay reachable while no project links them
  (`openWhenUnlinked`). A new data source is created with `openWhenUnlinked: false`: Admin-only until a project links
  it. With `Beacon:Authorization:RequireProjectMembership` every unlinked data source is Admin-only.
- **Shared data sources.** A data source linked to an open project is reachable through it by everyone with a role, even
  when a restricted project links it too. The project page warns Admins about such sources before they restrict a
  project, and lists every other project that links them.
- **Shared state.** Relationships, metadata description edits and a forced metadata refresh change what every linking
  project sees, so they need Write in every live project that links the data source, or an Admin; a DataSourceAdmin
  needs Write (or Manage) only in a project it acts through. Curating that state (verifying a relationship, deleting a
  verified one, applying or rejecting a documentation patch that writes a description) needs the project admin role in
  every live project that links the data source, the DataSourceAdmin permission, or an Admin (see
  [Who curates grounding](/mcp/knowledge-base/#who-curates-grounding)).
- **Shared guardrails.** A data source several live projects link runs MCP SQL, and is grounded and sampled, with the
  strictest guardrails among those projects (read-only enforcement, PII detection and patterns, row limits, value
  grounding and its probe budget, sample-value collection); an Admin sees them per source on the project page. See
  [Guardrails of shared data sources](/security/authorization/#guardrails-of-shared-data-sources).
- **One database login per boundary.** Project access decides who may run SQL through Beacon, not what the login can
  read. Use a login per data source that sees only what its projects should see, rather than one broad login shared by
  several boundaries: Beacon refuses names in another database, but a view, synonym or function in the source's own
  database still reads whatever the login may.

## Read-only guarantees per engine

Every statement Beacon runs on a data source first passes its SQL checks (a regex guardrail, a read-only parser check in
the engine's dialect, the configured-database rule and a row cap; see
[How a query runs](/features/queries/#how-a-query-runs)) and then runs on the provider's read-only path. What that path
adds at the database differs by engine, and on every engine it is defence in depth, not a replacement for the data
source's own permissions: **connect every data source Beacon reads with a login that can only read.**

| Engine | What the database itself does on Beacon's read-only path | Data-source badge |
|---|---|---|
| PostgreSQL | Session `default_transaction_read_only = on` plus a `READ ONLY` transaction, and the statement is sent as a batch of exactly one command, so the driver and the server refuse a second statement: the server refuses any write. | Enforced by the engine; still use a read-only login |
| MySQL / MariaDB | `SET SESSION TRANSACTION READ ONLY` plus `START TRANSACTION READ ONLY` on a connection without statement batches, rolled back afterwards: the server refuses any write. | Enforced by the engine; still use a read-only login |
| SQL Server | No read-only session or transaction mode exists. The statement runs inside a transaction that is always rolled back, so a write the SQL checks missed is undone rather than committed, but not refused, and work outside the transaction (a sequence value, a call to another server) is not undone. A statement that ends that transaction itself fails the run: its result is discarded and the security log records `ReadOnlyTransactionEnded` (7116, the data source id). | **Read-only login required** |
| Azure Synapse | No read-only mode; dedicated and serverless pools differ in what transactions they allow, so the statement runs plainly. | **Read-only login required** |
| Snowflake | No read-only session (DDL commits on its own); the statement runs plainly. | **Read-only login required** |
| BigQuery | No read-only session; the connector also requires a dry run to report statement type `SELECT`. | **Read-only login required** |
| Databricks | No read-only session; the connector re-checks the SQL with its own parser and the configured catalog. | **Read-only login required** |

On every engine without a database-level guarantee, Beacon's SQL checks are the only thing between a statement and the
data source's own permissions, so a **login (role, service account, token) that can only read** is the boundary. The
list and the data source page mark such sources, and Beacon logs a warning (naming the data source by id only) when one
is saved and when the host starts, except a [migration write target](#migration-write-targets), which Beacon never
reads and labels as one instead. The `databaseReadOnlyEnforced` field of `GET /beacon/api/data-sources` and
`GET /beacon/api/data-sources/{id}` reports it: `true` (the engine refuses writes), `false` (it does not; use a
read-only login) or `null` (the source runs no SQL, such as CloudWatch or an API source).

A SQL Server query over memory-optimized tables runs inside that rolled-back transaction, which such tables accept only
under snapshot isolation: if it fails with error 41368, turn on `MEMORY_OPTIMIZED_ELEVATE_TO_SNAPSHOT` for the database.

The column sample values collected during a metadata refresh are read on the same read-only path.

## Migration write targets

The one exception to read-only logins is a **migration write target**: a data source an Admin marked as the destination
of [data migrations](/features/data-migration/#migration-write-targets) (data source page → **Migration writes** → **Mark as migration write target**, or
`PUT /beacon/api/data-sources/{id}/migration-writes` with `{ "allowMigrationWrites": true }`; Admin only, and marking
needs `Beacon:Migrations:Enabled`). **Return to a read source** (or `{ "allowMigrationWrites": false }`) unmarks it. Beacon never reads a write target: query steps, previews,
subscriptions, data quality, MCP tools, AI grounding, documentation and metadata scans refuse it or leave it out, and its
catalog is not offered, so its login may hold the grants a migration needs to write
([destination privileges](/features/data-migration/#destination-privileges)). The list and the page label it
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
- Marking a data source clears the column sample values Beacon collected through its login.
- Before you return a write target to a read source, change its login back to one that can only read: from then on
  Beacon reads it again. The page asks for confirmation and says so.

## Read-only logins

Create a dedicated read-only login for every data source Beacon reads (every one except a
[migration write target](#migration-write-targets)). On SQL Server, Azure Synapse, Snowflake, BigQuery and Databricks
this is the only database-side guarantee that Beacon cannot write.

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

Beacon refuses linked-server (four-part) names, and a three-part `database.schema.table` name unless it names the data
source's own configured database. A synonym or view can still reach other databases the login can see, so don't map the
Beacon login on any linked server, don't grant it `ALTER ANY LINKED SERVER`, and give it access only to the databases
Beacon should read.

**MySQL:**

```sql
CREATE USER 'beacon_readonly'@'%' IDENTIFIED BY 'strong-password';
GRANT SELECT ON your_database.* TO 'beacon_readonly'@'%';
```

**Snowflake:** a role with `USAGE` on the warehouse, database and schemas and `SELECT` on the tables (no `OWNERSHIP`,
`INSERT`, `UPDATE`, `CREATE` or `OPERATE`), set as the user's default role or as `role=` in the connection.

**BigQuery:** a service account with `roles/bigquery.dataViewer` on the datasets and `roles/bigquery.jobUser` on the
project, not `dataEditor` or `admin`.

**Databricks:** a token for a principal with `USE CATALOG`, `USE SCHEMA` and `SELECT` only, and `CAN USE` on the SQL
warehouse.

**CloudWatch:** Beacon's own CloudWatch features read only the log groups the data source configures, so restrict the
IAM role to `logs:StartQuery`, `logs:GetQueryResults` and `logs:StopQuery` on those log groups, plus
`logs:DescribeLogGroups` for **Test connection**.
CloudWatch sources cannot be queried through MCP `query`.

## Troubleshooting

**Test connection fails.** Check network reachability from the Beacon host (`nc -vz db-host 5432`, `1433`, `3306`), the
server's remote-connection and TLS settings, and the credentials. A message that starts with "Connection refused" names
the transport option to change (see [Transport security (TLS)](#transport-security-tls)) or the engine the server
actually runs (see [Engine check](#engine-check)).

**Certificate errors.** The server's certificate must chain to a CA the Beacon host trusts and name the host in the
connection string. Install the issuing CA on the Beacon host, or pin it in the connection (`Root Certificate` on
PostgreSQL, `SslCa` on MySQL, `HostNameInCertificate` or `ServerCertificate` on SQL Server and Azure Synapse). Do not
turn certificate checks off: Beacon refuses such a connection.

**Permission denied on a table.** Check the login's grants:

```sql
-- PostgreSQL
SELECT grantee, table_schema, table_name, privilege_type
FROM information_schema.role_table_grants
WHERE grantee = 'beacon_readonly';
```

**Delete is refused.** Live queries or migration jobs still use the data source; the message names them.

**Refresh answers 429.** The data source was refreshed within the cooldown; try again later.

## Related

- [Queries](/features/queries/): run SQL against a data source
- [Data Migration](/features/data-migration/): copy data into a migration write target
- [Knowledge Base & Grounding](/mcp/knowledge-base/): what Beacon builds from this metadata
- [Data-source connectors](/integrate/connectors/): registering connectors in the host

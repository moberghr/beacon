---
title: Host DbContext
description: Expose the host application's own EF Core DbContext as a read-only Beacon data source — default-deny table allow-list, column exclusion and masking, no stored connection string.
---

When Beacon is embedded in an existing .NET application, that application's own database can be exposed to
Beacon (and through it to MCP agents) straight from its EF Core model. No connection string is stored, the
metadata comes from the model, and only the tables you allow-list can be read.

## Quick start

```csharp
var beacon = builder.Services.AddBeaconServices(builder.Configuration, o => { /* as today */ });

beacon.ExposeDbContext<NetgiroContext>(o => o
    .ReadOnlyConnection("BeaconReadOnly")            // ConnectionStrings:BeaconReadOnly — a read-only SQL login
    .AllowTables("Loan", "LoanInstallment", "Customer")
    .AllowEntity<Merchant>()
    .ExcludeColumns(x => x.Table == "Customer" && x.Name == "KycNotes")
    .MaskColumns(x => x.Name is "SSN" or "AccountNumber"));

beacon.AddSqlServerConnector();
beacon.UseSqlServer();                                // ends the builder chain — call ExposeDbContext first

var app = builder.Build();
// ... apply migrations ...
await app.Services.SyncBeaconHostDataSourcesAsync();  // creates / refreshes the data source
```

`SyncBeaconHostDataSourcesAsync` is a plain startup call (not a hosted service). It is idempotent: it creates the
project (`ProjectName`, default = the data-source name), the data source and the project link on first run, and
on later runs rewrites metadata only when the hash of the exposed model slice changed.

When the host also ships documentation with [`ExposeDocs`](/features/host-docs/), call
`SyncBeaconHostAsync()` instead: it runs this data-source sync and then imports the documents, as the single
startup entrypoint. `SyncBeaconHostDataSourcesAsync` keeps working on its own.

## Options

| Option | Meaning |
|---|---|
| `Name` | Data-source name. Default: the context type name. Also the stable key (`efcore:{Name}`). |
| `ProjectName` | Project to create or attach to. Default: `Name`. |
| `Description` | Description used when the project is created. |
| `ReadOnlyConnectionStringName` / `ReadOnlyConnection(name)` | **Required.** Name of a `ConnectionStrings` entry. Validated at startup; resolved from configuration at execution time; never persisted. |
| `Engine` | Normally inferred from the EF provider. Only SQL Server and PostgreSQL are supported. |
| `AllowTables("Table", "schema.Table")` | Allow-list by store name (case-insensitive). Unknown names fail startup. |
| `AllowEntity<T>()` | Allow-list the table or view an entity maps to. |
| `AllowAllTables()` | Expose everything. Deliberately explicit — prefer an allow-list. |
| `ExcludeColumns(Func<HostColumn, bool>)` | Invisible in metadata **and** rejected at execution. |
| `MaskColumns(Func<HostColumn, bool>)` | Visible in metadata (flagged `[PII: values are masked in query results]`); values masked in every result. Exclusion wins. |
| `IncludeSecretLikeColumn("Table.Column")` | Lifts the secret-like hard-exclude for one column. Logged as a warning at every sync. |
| `AllowFunctions("fn", …)` | Allows extra functions (unqualified name, case-insensitive) on top of the built-in allow-list. Only name functions that read nothing but their arguments. Catalog / settings / file / remote functions (`pg_*`, `xp_*`, `sp_*`, `fn_*`, `OPENROWSET`, `query_to_xml`, `row_to_json` …) are refused at startup. |

`HostColumn` gives `Schema`, `Table`, `Name` (column), `ClrType`, `EntityType` and `PropertyName`.

### Secret-like hard-exclude

Regardless of your predicates, a column is excluded when its column or property name
**contains** `password`, `passwd`, `secret`, `token`, `apikey`, `api_key`, `privatekey` or `private_key`, or has a
**whole word** (camelCase / snake_case) `pin`, `salt`, `hash`, `otp` or `refresh` — so `PasswordHash`, `PinCode`,
`OtpSeed` and `RefreshToken` are excluded while `Shipping` is not. Only `IncludeSecretLikeColumn` lifts it.

## Metadata

Read from the EF design-time model without opening a connection: tables and views (`ToTable` / `ToView`),
schemas, column names and store types, nullability, max length, primary keys, indexes, and **foreign keys**
(when the principal table is also exposed — they feed the schema-relationship graph and join suggestions).
Descriptions come from `HasComment`, falling back to XML documentation (`<summary>`) when the entity assembly
ships its `.xml` file (`<GenerateDocumentationFile>true</GenerateDocumentationFile>`). Owned types and table
splitting are merged into their table; entities mapped to SQL queries or functions are skipped; keyless views are
included when allow-listed.

A live catalog scan and sample-value collection are never run for a host data source — "Refresh metadata" in the
UI is refused; restart the host (or call the sync again) instead.

## Security model

- **No stored secret (§1.1).** `EncryptedConnectionData` holds only an encrypted reference,
  `{"hostConnectionStringName":"BeaconReadOnly"}`. The real string is read from `IConfiguration` whenever a
  connection is opened (`IDataSourceConnectionResolver`, the single decrypt-and-resolve point).
- **Host-owned.** The data source is marked `IsReadOnly`, cannot be edited or deleted through the REST API/UI
  (the request is refused), and cannot be used by migration jobs. Descriptions, glossary, golden queries and the
  knowledge graph work as for any other data source.
- **Policy enforced at execution.** Every SQL statement against a host data source is checked against the
  exposed slice, in the execution gate (MCP `query`, `dry_run`, cross-source, eval — a violation blocks regardless
  of `BlockOnSchemaFailure`) **and** again in the database provider (every path: `ask`, ad-hoc UI queries, value
  grounding, documentation sampling), in saved-query steps and in data-quality rules. The check parses the SQL in
  the data source's own dialect and resolves every name the way the database does:
  - only a single `SELECT`; every table must be allow-listed **and schema-qualified** (`dbo.Customer`,
    `public."Customer"`) — an unqualified name is rejected with `Qualify the table: dbo.Customer`, because the
    connection's real default schema / `search_path` may differ from the model's. Three-part / linked-server names,
    table-valued functions, `OPENROWSET` / `OPENJSON`, `SELECT INTO`, `FOR JSON/XML`, locking clauses, `NATURAL`
    joins and temporal `FOR SYSTEM_TIME` are rejected;
  - identifiers compare as the engine compares them (PostgreSQL: unquoted names fold to lower case, quoted names are
    exact; SQL Server: case-insensitive) — for tables, CTE names, aliases and columns alike, so a CTE never hides a
    real table (`WITH "AuditLog" AS (…) SELECT … FROM auditlog` is rejected);
  - names resolve with per-`SELECT` scopes: an unqualified column must be an exposed column of a relation in that
    `SELECT`'s own `FROM`; a correlated (outer) reference must be qualified with the outer alias; output aliases are
    visible only to the same `SELECT`'s `ORDER BY` (and PostgreSQL's `GROUP BY`) and to outer queries through a
    derived table's / CTE's columns. A bare relation name is never a column, so whole-row references
    (`SELECT c FROM … c`, `row_to_json(c)`, `(c).col`) are rejected; unknown and excluded columns are rejected
    anywhere; a table alias may be used only once per query;
  - `SELECT *` / `t.*` is rejected with a list of the exposed columns to use instead;
  - functions must be on a per-dialect **allow-list** of built-in string, date/time, math, conditional, conversion,
    aggregate, window and (on column values) JSON functions, plus any `AllowFunctions` entries; everything else —
    user-defined functions included — is rejected. Bind parameters are `@p0`, `@p1` … on both engines (the bare
    PostgreSQL `@` operator is rejected; use `abs()`), and a statement never runs with a parameter unbound;
  - a masked column may appear **only** as a direct projection (optionally aliased — through CTEs, derived tables
    and `UNION ALL` the mask follows the value to its output key), inside `COUNT(col)`, or in `col IS NULL` /
    `col IS NOT NULL`. Any other use — `WHERE`, `JOIN … ON`/`USING`, `GROUP BY`, `ORDER BY` (by name, alias or
    ordinal), `DISTINCT`, `UNION`/`INTERSECT`/`EXCEPT`, window specifications, `COUNT(DISTINCT …)`, any other function,
    `CASE`, casts, or a subquery used as a value — is rejected, so masked values cannot be probed.
  Host-masked values are masked **fully** in every result (`***`; `NULL` stays `NULL`).
- **Errors stay generic.** When a statement fails on the host database the caller gets
  `Query failed on the host database.` and the log records only the exception type — server messages can quote
  column values.
- **Read-only login.** SQL Server has no read-only transaction mode, so `SupportsDatabaseReadOnlyEnforcement`
  stays `false`; the parser gates plus the dedicated read-only login are the guarantee. On PostgreSQL the
  `READ ONLY` transaction backstop applies as usual.

### Known limits

- Columns that exist in the database but not in the EF model are unknown to Beacon. Every reference to them is
  rejected, but grant the read-only login `SELECT` only on the allow-listed tables (and `DENY` secret columns) as a
  second layer.
- A function added with `AllowFunctions` runs with the read-only login's rights; the policy cannot see what it
  reads. Do not grant the login `EXECUTE` on anything else.
- Masked columns still reveal whether a value is `NULL` (`IS NULL`, `COUNT(col)`); exclude a column when even that
  must stay hidden.
- On case-sensitive SQL Server collations, identifiers are still compared case-insensitively (a mismatch fails on
  the server rather than widening access).
- Some valid SQL is refused on purpose: unqualified correlated references, the same alias in an inner and an outer
  query, `NATURAL` joins, `DISTINCT`/set operations over masked columns.

## Netgiro-style example

```csharp
beacon.ExposeDbContext<NetgiroContext>(o =>
{
    o.Name = "Netgiro";
    o.ProjectName = "Netgiro Back Office";
    o.ReadOnlyConnection("BeaconReadOnly")
        .AllowTables("Loan", "LoanInstallment", "Merchant", "Customer")
        .ExcludeColumns(x => x.Table == "Customer" && x.Name.StartsWith("Kyc"))
        .MaskColumns(x => x.Name is "SSN" or "AccountNumber" or "Email" or "Phone");
});
```

`User.Secret`, `ProviderIntegrationKey.SecretKey` and `Customer.KvikaAuthorizationToken` are excluded by the
hard-exclude even if their tables are allow-listed; the kennitala (`SSN`) columns are visible to the agent as
PII-flagged columns and masked in every result.

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
  the data source's own dialect and walks the whole AST:
  - only `SELECT`; every table must be allow-listed (schema-qualified names must match; unqualified names resolve
    to the default schema; three-part / linked-server names, table-valued functions, `OPENROWSET`, `SELECT INTO`,
    `FOR JSON/XML`, schema-qualified function calls and query-running functions such as `query_to_xml` or `pg_*`
    are rejected);
  - CTE names are scoped exactly (a CTE never hides a real table of the same name outside its scope);
  - every column reference must be an exposed column of a table the query reads, or a name the query itself
    defines; excluded columns are rejected anywhere (projection, predicate, ordering, subquery), and whole-row
    references (`row_to_json(t)`) are rejected;
  - `SELECT *` / `t.*` is rejected with a list of the exposed columns to use instead;
  - masked columns may be projected directly (optionally aliased, through CTEs, subqueries and `UNION`) or used in
    predicates and `COUNT`; inside any other expression they are rejected, so every masked value leaves the query
    under a known key and is masked with the same masking the PII guardrail uses.
- **Read-only login.** SQL Server has no read-only transaction mode, so `SupportsDatabaseReadOnlyEnforcement`
  stays `false`; the parser gates plus the dedicated read-only login are the guarantee. On PostgreSQL the
  `READ ONLY` transaction backstop applies as usual.

### Known limits

- Masking protects returned values, not predicates: `WHERE SSN LIKE '01%'` is allowed and can be used as an
  oracle. Use `ExcludeColumns` for anything that must be unreachable.
- Columns that exist in the database but not in the EF model are unknown to Beacon. Direct references are
  rejected, but grant the read-only login `SELECT` only on the allow-listed tables (and `DENY` secret columns) as a
  second layer.
- Unqualified function calls are allowed apart from a deny-list; do not grant the read-only login `EXECUTE` on
  user functions. PostgreSQL `search_path` entries other than the default schema are not modelled.
- On case-sensitive SQL Server collations, identifiers are still compared case-insensitively.

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

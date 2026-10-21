---
title: Host DbContext
description: "Expose the host application's own EF Core DbContext as a read-only Beacon data source: default-deny table allow-list, column exclusion and masking, no stored connection string."
---

When Beacon is embedded in an existing .NET application, that application's own database can be exposed to
Beacon (and through it to MCP agents) straight from its EF Core model. No connection string is stored, the
metadata comes from the model, and only the tables you allow-list can be read.

## Quick start

```csharp
var beacon = builder.Services.AddBeaconServices(builder.Configuration, o => { /* as today */ });

beacon.ExposeDbContext<ShopDbContext>(o => o
    .ReadOnlyConnection("BeaconReadOnly")            // ConnectionStrings:BeaconReadOnly: a read-only SQL login
    .AllowTables("Loan", "LoanInstallment", "Customer")
    .AllowEntity<Merchant>()
    .ExcludeColumns(x => x.Table == "Customer" && x.Name == "KycNotes")
    .MaskColumns(x => x.Name is "SSN" or "AccountNumber"));

beacon.AddSqlServerConnector();
beacon.UseSqlServer();                                // ends the builder chain: call ExposeDbContext first

var app = builder.Build();
// ... apply migrations ...
await app.Services.SyncBeaconHostDataSourcesAsync();  // creates / refreshes the data source
```

`SyncBeaconHostDataSourcesAsync` is a plain startup call (not a hosted service). It is idempotent: it creates the
project (`ProjectName`, default = the data-source name), the data source and the project link on first run, and
on later runs rewrites metadata only when the hash of the exposed model slice changed.

When the host also ships documentation with [`ExposeDocs`](/mcp/host-docs/), call
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
| `AllowAllTables()` | Expose everything. Deliberately explicit; prefer an allow-list. |
| `ExcludeColumns(Func<HostColumn, bool>)` | Invisible in metadata **and** rejected at execution. |
| `MaskColumns(Func<HostColumn, bool>)` | Visible in metadata (flagged `[PII: values are masked in query results]`); values masked in every result. Exclusion wins. |
| `IncludeSecretLikeColumn("Table.Column")` | Lifts the secret-like hard-exclude for one column. Logged as a warning at every sync. |
| `AllowFunctions("fn", …)` | Allows extra functions (unqualified name, case-insensitive) on top of the built-in allow-list. Only name functions that read nothing but their arguments. Catalog / settings / file / remote functions (`pg_*`, `xp_*`, `sp_*`, `fn_*`, `OPENROWSET`, `query_to_xml`, `row_to_json` …) are refused at startup. |

`HostColumn` gives `Schema`, `Table`, `Name` (column), `ClrType`, `EntityType` and `PropertyName`.

### Secret-like hard-exclude

Regardless of your predicates, a column is excluded when its column or property name
**contains** `password`, `passwd`, `secret`, `token`, `apikey`, `api_key`, `privatekey` or `private_key`, or has a
**whole word** (camelCase / snake_case) `pin`, `salt`, `hash`, `otp` or `refresh`, so `PasswordHash`, `PinCode`,
`OtpSeed` and `RefreshToken` are excluded while `Shipping` is not. Only `IncludeSecretLikeColumn` lifts it.

## Metadata

Read from the EF design-time model without opening a connection: tables and views (`ToTable` / `ToView`),
schemas, column names and store types, nullability, max length, primary keys, indexes, and **foreign keys**
(when the principal table is also exposed; they feed the schema-relationship graph and join suggestions).
Descriptions come from `HasComment`, falling back to XML documentation (`<summary>`) when the entity assembly
ships its `.xml` file (`<GenerateDocumentationFile>true</GenerateDocumentationFile>`). Owned types and table
splitting are merged into their table; entities mapped to SQL queries or functions are skipped; keyless views are
included when allow-listed.

A live catalog scan and sample-value collection are never run for a host data source; "Refresh metadata" in the
UI is refused; restart the host (or call the sync again) instead.

## Security model

- **No stored secret (§1.1).** `EncryptedConnectionData` holds only an encrypted reference,
  `{"hostConnectionStringName":"BeaconReadOnly"}`. The real string is read from `IConfiguration` whenever a
  connection is opened (`IDataSourceConnectionResolver`, the single decrypt-and-resolve point).
- **Host-owned.** The data source is marked `IsReadOnly`, cannot be edited or deleted through the REST API/UI
  (the request is refused), and cannot be used by migration jobs. Descriptions, glossary, golden queries and the
  knowledge graph work as for any other data source.
- **Policy enforced at execution.** Every SQL statement against a host data source is checked against the
  exposed slice, in the execution gate (MCP `query`, `dry_run`, cross-source, eval; a violation blocks regardless
  of `BlockOnSchemaFailure`) **and** again in the database provider (every path: `ask`, query previews, value
  grounding, documentation sampling), in saved-query steps and in data-quality rules. The check parses the SQL in
  the data source's own dialect and resolves every name the way the database does:
  - only a single `SELECT`; every table must be allow-listed **and schema-qualified** (`dbo.Customer`,
    `public."Customer"`), an unqualified name is rejected with `Qualify the table: dbo.Customer`, because the
    connection's real default schema / `search_path` may differ from the model's. Three-part / linked-server names,
    table-valued functions, `OPENROWSET` / `OPENJSON`, `SELECT INTO`, `FOR JSON/XML`, locking clauses, `NATURAL`
    joins and temporal `FOR SYSTEM_TIME` are rejected;
  - identifiers compare as the engine compares them (PostgreSQL: unquoted names fold to lower case, quoted names are
    exact; SQL Server: case-insensitive), for tables, CTE names, aliases and columns alike, so a CTE never hides a
    real table (`WITH "AuditLog" AS (…) SELECT … FROM auditlog` is rejected);
  - on PostgreSQL, every identifier is read as the server names it: an identifier longer than 63 bytes in UTF-8 is
    shortened to 63 bytes on a character boundary, as PostgreSQL silently does (quoted names too, output aliases
    included), and compared and masked by that name. Only what the server would read differently than written is
    rejected: two different identifiers of the statement that are the same in their first 63 bytes (PostgreSQL would
    read them as one; use names that differ sooner, or, when the host's own names collide, expose a view or rename the
    column on the host), and an unquoted identifier with a non-ASCII letter that changes case, such as `Šifra` or the
    Kelvin sign (PostgreSQL folds only `A`–`Z`; write it in lower case or quote it, `"Šifra"`);
  - names resolve with per-`SELECT` scopes: an unqualified column must be an exposed column of a relation in that
    `SELECT`'s own `FROM`; a correlated (outer) reference must be qualified with the outer alias; output aliases are
    visible only to the same `SELECT`'s `ORDER BY` (and PostgreSQL's `GROUP BY`) and to outer queries through a
    derived table's / CTE's columns. A bare relation name is never a column, so whole-row references
    (`SELECT c FROM … c`, `row_to_json(c)`, `(c).col`) are rejected; unknown and excluded columns are rejected
    anywhere; a table alias may be used only once per query;
  - on PostgreSQL a bare `GROUP BY` name is read the way the server reads it: as a column of the `SELECT`'s own
    relations first, and as an output alias only when none of them has a column of that name. A table can have
    columns the policy excludes or the model does not map, so an output alias in `GROUP BY` is accepted only when the
    `SELECT` reads derived tables or CTEs alone; over a table, group by the expression itself or by its position
    (`GROUP BY 1`);
  - on PostgreSQL, catalog types (`regclass`, `regrole`, `regproc`, `regtype` … and their arrays, `regclass[]` or
    `_regclass`, in any case, quoted or schema-qualified) are rejected as casts (`CAST(… AS regclass)`,
    `'…'::regclass`) and as typed literals (`regclass '…'`), and a string where a name belongs (`SELECT col 'x'`,
    which PostgreSQL reads as a typed literal) is rejected — quote names with double quotes;
  - `SELECT *` / `t.*` is rejected with a list of the exposed columns to use instead;
  - functions must be on a per-dialect **allow-list** of built-in string, date/time, math, conditional, conversion,
    aggregate, window and (on column values) JSON functions, plus any `AllowFunctions` entries; everything else,
    user-defined functions included, is rejected. Bind parameters are `@p0`, `@p1` … on both engines (the bare
    PostgreSQL `@` operator is rejected; use `abs()`), and a statement never runs with a parameter unbound;
  - string-building functions take only bounded literals, so one call cannot build a huge value:
    - the count of `repeat` (`REPLICATE` on SQL Server) and the width of `lpad` / `rpad` must be a number literal of
      at most 10,000 (a column, parameter or expression is rejected; a negative number passes, since it builds
      nothing);
    - the string `repeat` / `REPLICATE` repeats must be a plain string literal of at most 100 characters, so a column
      or a nested call cannot multiply the length (`lpad` / `rpad` cut their string to the width, so theirs is free);
    - PostgreSQL's `format()` needs a string-literal format string whose field widths add up to at most 10,000 (a `*`
      width, taken from an argument, is rejected);
    - SQL Server's `FORMAT` needs a string-literal format string with no number above 999 (a precision such as
      `N999999999` is rejected).

    Other costly work (regular expressions, large joins and aggregates) is bounded by the statement timeout and the
    row cap, not by the check;
  - a masked column may appear **only** as a direct projection (optionally aliased, through CTEs, derived tables
    and `UNION ALL` the mask follows the value to its output key), inside `COUNT(col)`, or in `col IS NULL` /
    `col IS NOT NULL`. Any other use (`WHERE`, `JOIN … ON`/`USING`, `GROUP BY`, `ORDER BY` (by name, alias or
    ordinal), `DISTINCT`, `UNION`/`INTERSECT`/`EXCEPT`, window specifications, `COUNT(DISTINCT …)`, any other function,
    `CASE`, casts, or a subquery used as a value) is rejected, so masked values cannot be probed.
  Host-masked values are masked **fully** in every result (`***`; `NULL` stays `NULL`). The result is masked by
  **output position**, not by column name: the check knows which columns of the statement carry a masked value, and
  the provider masks those columns of the result whatever the database calls them: under any alias, a repeated
  name or another case. Names serve only as a consistency check that fails closed: a result column the database names
  differently than the check derived is masked too (logged as a warning with the data source id and counts), and a
  result whose column count differs from the statement's is discarded (`The result's columns did not line up with
  the columns the host data source policy checked, so the result was discarded.`). Every path that runs SQL on a host
  data source reads it through the provider: previews (paged ones included), subscriptions and their attachments,
  MCP tools, saved-query tools and the rows the in-memory join reads, so all of them return the masked values.
- **Errors stay generic.** When a statement fails on the host database the caller gets
  `Query failed on the host database.` and the log records only the exception type and the driver's error code;
  server messages can quote column values.
- **Read-only login.** SQL Server has no read-only transaction mode, so `SupportsDatabaseReadOnlyEnforcement`
  stays `false`; the parser gates plus the dedicated read-only login are the guarantee. On PostgreSQL the
  `READ ONLY` transaction backstop applies as usual.

### Behaviour to know

- Beacon knows the columns of the EF model. A statement that references a column the model does not have is
  rejected. Grant the read-only login `SELECT` only on the allow-listed tables (and `DENY` secret columns) so the
  database applies the same boundary.
- A function added with `AllowFunctions` runs with the read-only login's rights. Grant the login `EXECUTE` only on
  the functions you allow.
- A masked column still answers `IS NULL` and `COUNT(col)`. Exclude the column instead when its presence must stay
  hidden too.
- Identifiers are compared case-insensitively, also on case-sensitive SQL Server collations; a name whose case does
  not match fails on the server.
- These forms are refused with a validation message: unqualified correlated references, the same alias in an inner
  and an outer query, `NATURAL` joins, `DISTINCT` or set operations over masked columns, and on PostgreSQL two
  identifiers that share their first 63 bytes or an unquoted identifier with a non-ASCII letter that changes case.
- The 63-byte rule assumes a stock PostgreSQL build (`NAMEDATALEN` 64) and counts UTF-8 bytes. SQL Server keeps
  names up to 128 characters as written, so no length rule applies there.

:::caution[Upgrading]
Host column masking now works by output position. On PostgreSQL host data sources, a statement with two identifiers
that are the same in their first 63 bytes, or with an unquoted identifier that has a non-ASCII letter that changes
case, is now refused with a validation message: use names that differ sooner, write the name in lower case or quote
it. A single identifier longer than 63 bytes is accepted and read as PostgreSQL shortens it. Check saved queries,
subscriptions and saved-query tools on PostgreSQL host data sources for such aliases. A result column the database
names differently than the check derived is now masked (the MCP `query` tool notes how many), and a host data source
result whose columns do not match the statement now fails instead of being returned.
:::

## Example: restricted tables and masked columns

```csharp
beacon.ExposeDbContext<ShopDbContext>(o =>
{
    o.Name = "Shop";
    o.ProjectName = "Shop Back Office";
    o.ReadOnlyConnection("BeaconReadOnly")
        .AllowTables("Loan", "LoanInstallment", "Merchant", "Customer")
        .ExcludeColumns(x => x.Table == "Customer" && x.Name.StartsWith("Kyc"))
        .MaskColumns(x => x.Name is "SSN" or "AccountNumber" or "Email" or "Phone");
});
```

`User.Secret`, `ProviderIntegrationKey.SecretKey` and `Customer.PartnerAuthorizationToken` are excluded by the
hard-exclude even if their tables are allow-listed; the national-id (`SSN`) columns are visible to the agent as
PII-flagged columns and masked in every result.

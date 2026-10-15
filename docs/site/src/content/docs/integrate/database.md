---
title: Metadata database
description: "Where Beacon keeps its own data: PostgreSQL with pgvector or SQL Server, the fixed beacon schema, how to create and migrate it, and how to hand Beacon your own NpgsqlDataSource."
---

Beacon stores its own data (users, projects, data sources, queries, subscriptions, runs, audit rows, grounding) in a **metadata database**. This is separate from the databases Beacon monitors, although it can live on the same server.

## Choose a provider

| | PostgreSQL | SQL Server |
|---|---|---|
| Package | `Moberg.Beacon.Core.PostgreSql` | `Moberg.Beacon.Core.SqlServer` |
| Registration | `.UsePostgreSql(...)` | `.UseSqlServer(...)` |
| Naming | snake_case tables and columns | Pascal case |
| Semantic retrieval | `pgvector` column + HNSW index | Vectors compared in memory |
| Extra requirement | The `vector` extension must be available on the server | None |

Both providers carry their own EF Core migrations and are kept in step: every schema change ships for both.

## The `beacon` schema

Everything lives in the schema `beacon`, including the migration history table (`beacon.__EFMigrationsHistory`). The schema is **not configurable**: migration snapshots name it, so `Beacon:Schema` set to anything other than `beacon` makes startup fail with an explanation. Leave the key unset.

## Registration

```csharp
// Connection string from ConnectionStrings:BeaconContext
.UsePostgreSql();

// Explicit connection string
.UsePostgreSql(connectionString);

// Your own NpgsqlDataSource: Aspire, Entra passwordless auth, a shared pool
.UsePostgreSql(sp => sp.GetRequiredService<NpgsqlDataSource>());
.UsePostgreSql(dataSource);

// SQL Server
.UseSqlServer();
.UseSqlServer(connectionString);
```

Every overload also takes a `schema` argument; it only accepts `"beacon"`.

`UsePostgreSql` / `UseSqlServer` register an `IDbContextFactory<BeaconContext>` and a scoped `BeaconContext` (Data Protection persists its key ring through it). Call them **last** in the `AddBeaconServices(...)` chain: they return `IServiceCollection`, and connectors, `ExposeDbContext` and `ExposeDocs` must come before.

### Bringing your own `NpgsqlDataSource`

When you pass a data source (or a factory resolved once from the root provider), Beacon:

- runs `SET search_path TO "beacon", public` on every connection it opens, so its unqualified SQL and the `vector` type resolve;
- does not need `UseVector()` on your data source, because it never binds the vector type;
- expects connection reset to stay on (no `No Reset On Close=true`) and `Multiplexing` off.

With a connection string, Beacon builds its own data source with `SearchPath=beacon,public`.

## Creating and migrating the schema

**Beacon does not migrate its schema by itself.** Create the **database** first (`CREATE DATABASE beacon;`, or `POSTGRES_DB=beacon` for the container image). Beacon creates the schema and tables, not the database. Then call `UseBeacon` once at startup, after `builder.Build()`:

```csharp
var app = builder.Build();

// Creates the beacon schema if missing, then applies pending EF Core migrations.
Beacon.Core.ServiceConfiguration.UseBeacon(app.Services, createSchema: true);
```

`UseBeacon` is a plain static method on `Beacon.Core.ServiceConfiguration` (several Beacon packages have a class called `ServiceConfiguration`; use the full name). It is synchronous.

| Situation | Approach |
|---|---|
| One instance, or a dev machine | `UseBeacon(app.Services, createSchema: true)` at startup. |
| Several instances starting together | Run migrations once, from a deployment step, a dedicated migrator process that calls `UseBeacon`, or an idempotent SQL script, rather than from every replica. |
| The application login cannot create schemas or extensions | Pre-create the `beacon` schema (and on PostgreSQL the `vector` extension) as an administrator, grant the application login ownership of the schema, then call `UseBeacon(app.Services)` without `createSchema`. |

### pgvector on PostgreSQL

One migration runs `CREATE EXTENSION IF NOT EXISTS vector;` and adds a `vector(384)` column with an HNSW index. The extension must be **available** on the server (`SELECT * FROM pg_available_extensions WHERE name = 'vector';` returns a row) and the migrating login must be allowed to create it, or an administrator must install it first.

- Azure Database for PostgreSQL – Flexible Server: add `VECTOR` to the `azure.extensions` server parameter.
- Amazon RDS / Aurora PostgreSQL: pgvector is available on current engine versions.
- Self-hosted: install the pgvector package for your PostgreSQL major version, then restart.

If the extension is installed in `public`, Beacon's search path (`beacon, public`) finds it.

### Generating SQL instead of migrating at runtime

Migrations live in the provider assemblies. To produce a script from a checkout of the Beacon repository:

```bash
dotnet tool install --global dotnet-ef
dotnet ef migrations script --idempotent \
  --project src/Beacon.Core.PostgreSql \
  --startup-project src/Beacon.SampleProject \
  --context PostgreSqlBeaconContext \
  --output beacon-postgresql.sql
```

The startup project needs `ConnectionStrings:BeaconContext` configured. `--context` is required because the sample host has more than one `DbContext`. The migrations create the `beacon` schema and the `vector` extension themselves.

## Sizing and housekeeping

- Run history, MCP audit rows and learning signals grow with use. Set `Beacon:Mcp:Audit:RetentionDays` and schedule the audit purge (see [Background jobs](/integrate/background-jobs/#recurring-maintenance-jobs)); learning signals are cleaned by the `CleanupOldSignals` job after the project's retention period (90 days by default).
- Back up the metadata database like any application database. It holds encrypted connection strings and tokens; they are useless without `Beacon:EncryptionKey`, so keep the key's backup separate.
- Beacon's own data access uses short-lived contexts from `IDbContextFactory<BeaconContext>`; size the pool for your job runner's worker count plus web traffic. The reference host caps Warp at `min(2 × CPU count, 20)` workers for this reason.

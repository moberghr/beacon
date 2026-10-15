# Beacon's database schema

Beacon keeps all of its own tables in one schema, `beacon`, on PostgreSQL and on SQL Server. The migration history
table is `beacon.__EFMigrationsHistory`. The schema is **not configurable**. (This file's name is historical: an
earlier design let a host pick the schema at runtime. That design is gone.)

## What `Beacon:Schema` does now

`Beacon:Schema` and the `schema` parameter of `UsePostgreSql(...)` / `UseSqlServer(...)` accept only `beacon`.
Leave them unset. Any other value throws `InvalidOperationException` when the provider is registered, before the
host starts. The message tells you to remove the setting and move any existing Beacon tables into `beacon`.

## Why the schema is fixed

Each provider's model snapshot names the schema. On `Database.Migrate()`, EF Core compares the runtime model with
the snapshot. With any other schema the two disagree, EF raises `PendingModelChangesWarning`, and `Migrate()`
throws before it applies anything. A new migration cannot fix that, because its snapshot names one schema too.
Rejecting other values at registration turns an opaque EF error at boot into a clear configuration error.

## Why migrations still go through a schema-aware SQL generator

The PostgreSQL migrations were scaffolded before the model had a default schema, so their operations carry no
schema at all. The committed files cannot be edited (migrations are append-only), so each provider replaces EF's
SQL generator with `SchemaAwareMigrationsSqlGenerator`. It rewrites operations whose schema is empty or the literal
`beacon` to the configured schema (always `beacon` now) when the SQL is generated. The shared logic is
`MigrationOperationSchemaRetargeter` in `Beacon.Core`.

The PostgreSQL migration `MoveTablesToConfiguredSchema` has empty `Up` and `Down` on purpose. It only records the
schema in the snapshot so that model and snapshot agree. Do not regenerate it. SQL Server needs no such migration:
its snapshot already declares `HasDefaultSchema("beacon")`.

On PostgreSQL, Beacon's connections also use the search path `beacon, public`, so the unqualified names in raw
migration SQL (`mcp_embeddings`, `vector`) resolve, and a pgvector installed in `public` is found.

## What this means for you

- Hosts: do not set `Beacon:Schema`. Create the schema with
  `Beacon.Core.ServiceConfiguration.UseBeacon(app.Services, createSchema: true)` or ahead of time.
- Contributors: scaffold new migrations with the default configuration, and follow [MIGRATIONS.md](MIGRATIONS.md).

See [Metadata database](https://moberghr.github.io/beacon/integrate/database/) in the docs.

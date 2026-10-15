# Database migrations

How to add and apply EF Core migrations for Beacon's own metadata database. This is a contributor guide; for
running Beacon in a host, see [Metadata database](https://moberghr.github.io/beacon/integrate/database/).

## Rules

- **Dual provider.** Every schema change needs a new migration in both `src/Beacon.Core.PostgreSql` and
  `src/Beacon.Core.SqlServer`. Give both the same name.
- **Append-only.** Never edit a migration that has been committed. Add a new one.
- Migrations live in `Data/Migrations/` of each provider project, next to `PostgreSqlBeaconContextModelSnapshot.cs`
  and `SqlServerBeaconContextModelSnapshot.cs`.
- PostgreSQL-only objects (the pgvector `embedding vector(384)` column and its HNSW index) go in through
  `migrationBuilder.Sql(...)` and stay out of the EF model and both snapshots.

## Prerequisites

- The EF Core CLI: `dotnet tool install --global dotnet-ef` (it is not a local tool in this repo).
- Run every command from the repository root.
- `src/Beacon.SampleProject` is the startup project. It must start far enough to build its services, so it needs
  `ConnectionStrings:BeaconContext` and `Beacon:EncryptionKey` in its configuration (user secrets work).

## Add a migration

1. Change the entities and their fluent configuration in `BeaconContext.OnModelCreating` (`src/Beacon.Core`).
2. Scaffold the PostgreSQL migration:

   ```bash
   dotnet ef migrations add AddWidgetTable \
     --project src/Beacon.Core.PostgreSql \
     --startup-project src/Beacon.SampleProject \
     --context PostgreSqlBeaconContext \
     --output-dir Data/Migrations
   ```

3. Write the SQL Server migration by hand. The same command with `--project src/Beacon.Core.SqlServer
   --context SqlServerBeaconContext` fails with `Unable to resolve service for type
   'DbContextOptions<SqlServerBeaconContext>'`: the sample host registers only `UsePostgreSql()`, and
   `src/Beacon.Core.SqlServer` has no `IDesignTimeDbContextFactory`. Add three things under
   `src/Beacon.Core.SqlServer/Data/Migrations/`:
   - the migration, timestamped one second after the PostgreSQL one (`20261014120000_…` → `20261014120001_…`);
   - the matching change in `SqlServerBeaconContextModelSnapshot.cs`;
   - its `.Designer.cs`: copy the updated SQL Server snapshot, change the class to
     `[Migration("<timestamp>_<Name>")] partial class <Name>` with `BuildTargetModel`, and add
     `using Microsoft.EntityFrameworkCore.Migrations;`.

Set a `defaultValue:` on every non-nullable `AddColumn`, and prefer `.HasDefaultValue(...)` in the fluent
configuration so the model, the snapshot and the migration agree.

To undo a migration you have not committed yet:

```bash
dotnet ef migrations remove --project src/Beacon.Core.PostgreSql --startup-project src/Beacon.SampleProject --context PostgreSqlBeaconContext
```

## Apply migrations

Beacon does not migrate itself, and the sample host does not migrate Beacon's schema either (it only migrates
Warp's). A host applies Beacon's migrations by calling, after `builder.Build()`:

```csharp
Beacon.Core.ServiceConfiguration.UseBeacon(app.Services, createSchema: true);
```

`createSchema: true` creates the `beacon` schema if it is missing, then `Database.Migrate()` runs.

From the CLI (PostgreSQL), create the schema first on a fresh database (`CREATE SCHEMA IF NOT EXISTS beacon;`),
then:

```bash
dotnet ef database update --project src/Beacon.Core.PostgreSql --startup-project src/Beacon.SampleProject --context PostgreSqlBeaconContext
```

Pass a migration name after `update` to move the database to that migration (reverting later ones). The CLI path
does not work for SQL Server through the sample host, for the reason above; apply SQL Server migrations through a
host that registers `UseSqlServer()` and calls `UseBeacon`.

The PostgreSQL migration `AddMcpEmbedding` runs `CREATE EXTENSION IF NOT EXISTS vector`, so the migrating login
must be able to create pgvector or it must already be installed (on Azure Flexible Server, allow `VECTOR` in
`azure.extensions`). Beacon puts `beacon, public` on the search path of its connections, including a host-owned
`NpgsqlDataSource`, so the migration's unqualified `mcp_embeddings` and `vector` resolve.

## List migrations

```bash
dotnet ef migrations list --project src/Beacon.Core.PostgreSql --startup-project src/Beacon.SampleProject --context PostgreSqlBeaconContext
```

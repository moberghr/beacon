# Data Layer

> §5.x — ORM, queries, connections. Loaded automatically by Claude Code.

## Context lifecycle

§5.1 **Inject `IDbContextFactory<BeaconContext>`, never `BeaconContext` directly.** Create a short-lived context per unit of work: `await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);`.

§5.2 **Abstract context, provider-specific subclasses.** `BeaconContext` is abstract. `PostgreSqlBeaconContext` and `SqlServerBeaconContext` are the concrete types. PostgreSQL also gets `UseSnakeCaseNamingConvention()`.

§5.3 **Schema is fixed at `"beacon"`.** NOT configurable — `Beacon:Schema` set to any other value is rejected at startup by `BeaconDatabaseConfiguration.RequireSupportedSchema`. Migration snapshots bake the schema in, so another value makes `Database.Migrate()` fail with `PendingModelChangesWarning` and no migration can fix it. All entities live in this schema.

## Configuration & entities

§5.4 **Fluent API only.** All entity configuration lives in `BeaconContext.OnModelCreating()` via private `Configure{Domain}Entities()` partials. NEVER use data annotations (`[Table]`, `[Key]`, `[Column]`, `[Required]`, etc.) and do NOT introduce `IEntityTypeConfiguration<T>` classes.

§5.5 **Entity base classes:**
- `BaseEntity` — `Id` (int), `CreatedTime` (default `DateTime.UtcNow`).
- `ArchivableBaseEntity : BaseEntity` — adds `ArchivedTime` (soft delete).
- `AuditableBaseEntity : BaseEntity` — adds `CreatedByUserId`, `ModifiedTime`, etc.

## Queries

§5.6 **`.Select(new ...)` over `.Include()`.** Project only the fields the caller needs. EF Core auto-joins for projection — you do NOT need `.Include()` next to a `.Select(new ...)` call. Adding it is dead code and hurts performance.

§5.7 **One `SaveChangesAsync()` per handler.** Services called by handlers must NOT call `SaveChanges` themselves — the handler owns the unit of work.

§5.8 **Pass `CancellationToken` to every async EF call.** `ToListAsync(cancellationToken)`, `FirstOrDefaultAsync(cancellationToken)`, `SaveChangesAsync(cancellationToken)`.

## Migrations

§5.9 **Dual-provider migrations are append-only.** Both `src/Beacon.Core.PostgreSql/Migrations/` and `src/Beacon.Core.SqlServer/Migrations/` need a new migration whenever an entity changes. NEVER edit a migration that has already been committed — add a new one.

§5.10 See `tasks/lessons.md` and the project memory file `migrations-workflow.md` for the exact `dotnet ef migrations add` invocations for each provider.

## Hot paths

§5.11 **Dapper is in use alongside EF Core** for metadata extraction (`*MetadataExtractor.cs`), bulk operations, and performance-critical raw SQL — see `src/Beacon.Core/Helpers/BulkHelpers.cs`, `src/Beacon.Core/Services/MigrationService.cs`, and the connector `JobRepository.cs` files. Do NOT migrate these to EF Core unless explicitly asked.

§5.12 **Bulk inserts/updates use `EFCore.BulkExtensions`.** Go through `BulkHelpers.cs`, not `context.AddRange` for >1k rows.

## Pitfalls

§5.13 **Empty-sequence aggregates blow up on Npgsql.** `Max()` / `Min()` on a possibly-empty filtered set translates to SQL that throws — guard with `.Any()` or use the `??` overload.

§5.14 **In-memory SQLite for cross-source joins.** Multi-step queries materialize intermediates into `Microsoft.Data.Sqlite` (in-memory) and run the final join there. Do not try to push cross-source joins into a single connector.

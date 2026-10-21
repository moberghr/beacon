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
- `IAttributedEntity` (an interface, not a base class) — `CreatedByUserId`, `ModifiedByUserId` (`Users.ExternalId` values, like an owner id) and `ModifiedTime`; implemented by `Query`, `Subscription`, `Recipient`, `DataContract` and `MigrationJob`. Set the creator where the entity is built and call `entity.RecordChangeBy(actor, timeProvider)` on every change, always from the request's `BeaconActor` (`IBeaconActorAccessor`), never from a request body; a bulk `ExecuteUpdate` sets `ModifiedByUserId` and `ModifiedTime` itself. Null for rows from before attribution was recorded.

## Queries

§5.6 **`.Select(new ...)` over `.Include()`.** Project only the fields the caller needs. EF Core auto-joins for projection — you do NOT need `.Include()` next to a `.Select(new ...)` call. Adding it is dead code and hurts performance.

§5.7 **One `SaveChangesAsync()` per handler.** Services called by handlers must NOT call `SaveChanges` themselves — the handler owns the unit of work. The one sanctioned exception is `ISecurityAuditRecorder.SaveWithEventsAsync`: it saves the handler's change, then the security audit events built from what that save produced (generated ids, the rows a bulk update touched), as two saves in one transaction — the caller's when one is open, else one it opens through the context's execution strategy — so the change and its events commit or roll back together. Use it only when an event needs the save's result; an event that needs nothing from it is staged with `StageAsync` before the handler's single save. Do not call `SaveChangesAsync` again after it, and do not wrap other saves into it.

§5.8 **Pass `CancellationToken` to every async EF call.** `ToListAsync(cancellationToken)`, `FirstOrDefaultAsync(cancellationToken)`, `SaveChangesAsync(cancellationToken)`.

## Migrations

§5.9 **Dual-provider migrations are append-only.** Both `src/Beacon.Core.PostgreSql/Migrations/` and `src/Beacon.Core.SqlServer/Migrations/` need a new migration whenever an entity changes. NEVER edit a migration that has already been committed — add a new one.

§5.10 See `tasks/lessons.md` and the project memory file `migrations-workflow.md` for the exact `dotnet ef migrations add` invocations for each provider.

## Hot paths

§5.11 **Dapper is in use alongside EF Core** for metadata extraction (`*MetadataExtractor.cs`), bulk operations, and performance-critical raw SQL — see `src/Beacon.Core/Services/MigrationService.cs`, `src/Beacon.Core/Services/SqlPageExecutor.cs` and `src/Beacon.Core/Services/InMemoryDatabaseManager.cs`. Do NOT migrate these to EF Core unless explicitly asked.

§5.12 **Migration bulk writes go through the destination engine's `IMigrationBulkWriter`** (`src/Beacon.Core/DataMigration/`), registered by its connector (`Add{Engine}Connector()`): PostgreSQL binary COPY, SQL Server `SqlBulkCopy`, MySQL multi-row parameterised `INSERT` (`ON DUPLICATE KEY UPDATE` for Upsert, `TRUNCATE TABLE` to clear — the server refuses a table another table's foreign key references — then `START TRANSACTION` again, since TRUNCATE commits implicitly: a run failing after the clear leaves the table empty), all on the run's connection and transaction. Never `context.AddRange` for >1k rows.

## Pitfalls

§5.13 **Empty-sequence aggregates blow up on Npgsql.** `Max()` / `Min()` on a possibly-empty filtered set translates to SQL that throws — guard with `.Any()` or use the `??` overload.

§5.14 **In-memory SQLite for cross-source joins.** Multi-step queries materialize intermediates into `Microsoft.Data.Sqlite` (in-memory) and run the final join there. Do not try to push cross-source joins into a single connector.

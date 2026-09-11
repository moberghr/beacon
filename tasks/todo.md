# Configurable default schema (both EF providers) — `feature/retention-lock`

Spec `docs/specs/2026-09-11-configurable-default-schema.md` · Plan `docs/plans/2026-09-11-configurable-default-schema.md` · Workflow `wf-20260911T114117Z-329a21`

Source: external audit of shipped `Moberg.Beacon` 4.1.0 (decompiled IL). Direction chosen by the engineer: **full symmetry** — PostgreSQL adopts `HasDefaultSchema` rather than keeping its `SearchPath`-only mechanism.

## B1 — Core: options extension + schema resolution
- [x] `BeaconSchemaOptionsExtension` + `ExtensionInfo` (service-provider hash includes schema)
- [x] `DbContextOptionsBuilder.UseBeaconSchema(string)`
- [x] `BeaconSchema.Resolve(DbContext)` — replaces reflection
- [x] `BeaconSchema.CreateSchemaStatement(provider, schema)` — validates identifier, dialect-specific idempotent DDL
- [x] `BeaconContext.OnModelCreating` resolves options → ctor arg → `"beacon"`, keeps `IsNullOrEmpty` guard
- [x] Tests: `UseBeaconSchema_RoundTripsThroughOptions`, `CreateSchemaStatement_*` (3)
- [x] Checkpoint: build + targeted unit tests

## B2 — SQL Server: generator, cache key, wiring
- [x] `SchemaAwareMigrationsSqlGenerator : SqlServerMigrationsSqlGenerator` (Schema / PrincipalSchema / NewSchema + nested `CreateTableOperation` ops)
- [x] `SchemaModelCacheKeyFactory : IModelCacheKeyFactory`
- [x] `SqlServerBeaconContext` — drop the `= "beacon"` ctor default
- [x] `UseSqlServer` — `UseBeaconSchema` + both `ReplaceService` calls
- [x] Tests: `SqlServerGenerator_RetargetsCreateTableSchema`, `..._RetargetsForeignKeyPrincipalSchema`, `ModelCacheKeyFactory_DiffersBySchema`, `..._EqualForSameSchema`
- [x] Checkpoint: build + SQL Server tests + `git status` over `Beacon.Core.SqlServer/Data/Migrations` empty (SC2)

## B3 — PostgreSQL: generator, cache key, wiring (symmetry)
- [x] `SchemaAwareMigrationsSqlGenerator : NpgsqlMigrationsSqlGenerator` (`null` is the common case here)
- [x] `SchemaModelCacheKeyFactory` (PG namespace; not shared — §2.4)
- [x] `UsePostgreSql` — `UseBeaconSchema` + both `ReplaceService` + history-table schema; **retain `SearchPath`** (R3)
- [x] Tests: `PostgreSqlContext_WithConfiguredSchema_QualifiesTables_Translates`, `..._WithEmptySchema_EmitsUnqualified_Translates`, `PostgreSqlGenerator_RetargetsNullSchema`
- [x] Checkpoint: build + PG tests + `git status` over `Beacon.Core.PostgreSql/Data/Migrations` empty (SC2)

## B4 — `UseBeacon` hardening
- [x] Schema via `BeaconSchema.Resolve`, not `GetProperty(...)`
- [x] Idempotent `CREATE SCHEMA` via `BeaconSchema.CreateSchemaStatement`; keep `ExecuteSqlRaw` + `EF1002` pragma (2026-06-11 lesson)
- [x] Delete the dead `GetSchemaFromContext`
- [x] Checkpoint: build + **full** `dotnet test`; `grep -c 'GetProperty(' src/Beacon.Core/ServiceConfiguration.cs` → 0 (SC6)

## Post-implementation review
- [x] Phase 3.5 spec-drift check against the JSON sidecar
- [x] Stage 1 — `compliance-reviewer` against the sealed spec
- [x] Stage 2 — reviewer set per rigor level
- [x] Confirm zero migration files modified across both providers (SC2)
- [x] Confirm no pre-existing-failure delta vs the Phase 2.9 baseline (SC7)

## Follow-ups raised by the same audit (NOT in this run)
- [ ] README quick-start symbols (`AddBeacon` → `AddBeaconServices`, `UseSqlServer` chaining, `UseBeaconUI` → `MapBeaconUi`) — both provider READMEs
- [ ] `AddBeaconApiServices()` should register the `BeaconApi` policy
- [ ] Semantico 3.7.0.1 → 4.1.0 upgrade path (SQL script + `[Obsolete]` shim + changelog for `IBeaconScheduler`/`IJobService` breaks)
- [ ] `Beacon:EncryptionKey` error message should name `Semantico:EncryptionKey`
- [ ] SPA sub-path hosting (configurable `<base href>`)
- [ ] Host should read `Beacon:Schema` instead of hardcoding `"semantico"`

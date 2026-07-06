# Phase 3 Batch 5e — Create Migration Job wizard

## Goal
Port the Blazor `CreateMigrationJob` page (`src/Beacon.UI/Components/Pages/DataMigration/CreateMigrationJob.razor`) to the React shell. Add the missing MediatR handler + REST endpoint.

## Scope shipped
The Blazor page is a 365-LOC `@page` covering create AND edit, with a `QueryStepBuilder` multi-step query UI, preview/execute against the data source, and a flow diagram. The React slot ships **the core single-step create flow**:

| Blazor capability | React Batch 5e | Note |
|---|---|---|
| Create job | Yes | 5-step `StepperDialog` |
| Edit job | Deferred | Different route + load handler — separate slot |
| Multi-step `QueryStepBuilder` | Deferred | Single SQL textarea instead. Backend already supports plain SQL via the legacy fallback in `LoadMigrationJob` |
| Step preview / execute | Deferred | Needs `IQueryExecutionPreviewService` MediatR exposure |
| `QueryFlowDiagram` | Deferred | Mermaid-style diagram component not yet ported |
| Validate-only button | Deferred | Inline Zod validation covers required-field path |
| Transformation script | Yes | Optional textarea on schedule step |

## Backend changes
- New handler `src/Beacon.Core/Handlers/DataMigration/CreateMigrationJobHandler.cs` — `internal sealed class` with primary-ctor injection of `IMigrationService`. Validates required fields and throws `InvalidOperationException` on bad input (per §2.9 / §9.8); delegates to the existing `MigrationService.CreateMigrationJob`.
- New records in the same file: `CreateMigrationJobCommand` (IRequest) and `CreateMigrationJobResult`. Naming matches `OpenApiContractTests` heuristic — `CreateMigrationJobCommand` strips to `CreateMigrationJob`.
- New endpoint `POST /beacon/api/migrations/jobs` in `src/Beacon.SampleProject/Endpoints/MigrationsEndpoints.cs` with `.WithName("CreateMigrationJob")` and `.Produces<CreateMigrationJobResult>(200)`.

No new migration; the `MigrationJob` entity and `MigrationService.CreateMigrationJob` already exist.

## Frontend changes
- `src/Beacon.SampleProject/web/src/routes/migration-history/queries.ts` — adds `MIGRATION_MODE` constants, `MIGRATION_MODE_LABEL`, `CreateMigrationJobPayload` / `CreateMigrationJobResponse` types, and `useCreateMigrationJob` mutation that POSTs via `fetchJson`. Invalidates `['migration-executions']` on success.
- `src/Beacon.SampleProject/web/src/routes/migration-history/CreateMigrationJobDialog.tsx` (new, ~340 LOC) — 5-step `StepperDialog`:
  1. **Basics** — name + description (Zod required)
  2. **Source** — pick a database-engine data source from `useDataSourcesQuery()`, write extraction SQL
  3. **Destination** — destination data source + table + `MigrationMode` select
  4. **Schedule & options** — cron (optional), max retries, timeout, enabled / validate flags, optional transformation script
  5. **Review** — read-only summary, submit
- `src/Beacon.SampleProject/web/src/routes/migration-history/MigrationHistoryPage.tsx` — adds **+ New job** button next to **Refresh**; opens the dialog.

## Tests
- `src/Beacon.SampleProject/web/src/routes/migration-history/CreateMigrationJobDialog.test.tsx` — Vitest + MSW. Two cases:
  - Walks all 5 steps and asserts the POST body.
  - Blocks advance past the Source step when SQL is empty (Zod validation).

Both pass. Full vitest suite: 11/11 green. Full dotnet test suite: 35/35 green, including `OpenApiContractTests.EveryMediatRHandlerIsExposedViaHttp`.

## Constraints honoured
- No Tailwind / shadcn — uses the project's `q-*` form classes and `btn` / `pill` / `stepper__review` design tokens.
- No fake/seed/demo data — real `useDataSourcesQuery()` populates the Source/Destination selects.
- In-app paths never `/app/...` — the dialog mounts at the existing `/migration-history` route.
- RHF + Zod 4. Form output shape matches the schema (`z.number().int().min(1, …)` on selects; `register('…', { valueAsNumber: true })` and `setValueAs` for cleanly converting empty `<select>` values to `0` so the `min(1)` message reads "Pick a source data source" instead of "expected number, received NaN").
- Mutation invalidates `['migration-executions']` and surfaces toast errors from `ApiError`.
- Backend handler is `internal sealed class` with primary-ctor injection; throws `InvalidOperationException`; endpoint named `CreateMigrationJob`.
- After build, `web/dist/` synced into `wwwroot/app/`; cleared `obj/**/*.dswa.cache.json` and `*.Up2Date` markers to avoid stale-chunk DSWA rot.

## Files touched
- `src/Beacon.Core/Handlers/DataMigration/CreateMigrationJobHandler.cs` (new)
- `src/Beacon.SampleProject/Endpoints/MigrationsEndpoints.cs`
- `src/Beacon.SampleProject/web/src/routes/migration-history/queries.ts`
- `src/Beacon.SampleProject/web/src/routes/migration-history/CreateMigrationJobDialog.tsx` (new)
- `src/Beacon.SampleProject/web/src/routes/migration-history/CreateMigrationJobDialog.test.tsx` (new)
- `src/Beacon.SampleProject/web/src/routes/migration-history/MigrationHistoryPage.tsx`
- `src/Beacon.SampleProject/wwwroot/app/**` (rebuilt SPA bundle)
- `ClaudePlans/ReactMigration-Phase3-Batch5e-Diff.md` (this file)

## Follow-up (next slot candidates)
- Edit-mode (`/migration-jobs/:id/edit`) and a migration-jobs list page distinct from migration-history.
- Multi-step query builder + step preview + flow diagram (large — likely its own slot).
- A `Validate` action that calls a future `ValidateMigrationJobCommand`.

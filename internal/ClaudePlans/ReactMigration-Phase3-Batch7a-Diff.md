# Phase 3 Batch 7a — Pre-cutover pages

Goal: ship React equivalents for the last Blazor pages blocking removal of `Beacon.UI` (Dashboards, AI Actors, Migration Jobs, Auth landing pages).

## Pages shipped

| Slug | Route(s) | Status |
| --- | --- | --- |
| dashboards | `/dashboards`, `/dashboards/:id`, `/dashboards/:id/edit` | List + viewer functional; builder ships as descriptive placeholder |
| ai-actors | `/ai-actors`, `/ai-actors/:id` | List (data-source scoped) + detail (hero KPIs + instructions); refine/plan-review/execution-history deferred |
| migration-jobs | `/migration-jobs` | Full list with delete; "+ New job" reuses existing `CreateMigrationJobDialog`; edit page deferred (see Deferrals) |
| auth | `/login`, `/logout`, `/setup`, `/error` | All four ship; rendered outside `RequireAuth` |

`MIGRATED_PAGES` adds `dashboards`, `ai-actors`, `migration-jobs` so the sidebar links into React. Auth pages are intentionally not in the list — the sidebar never points there.

## Backend additions

- `src/Beacon.Core/Handlers/DataMigration/GetMigrationJobsHandler.cs` — new query, projects EF entity to `MigrationJobListItem` with both source/destination data-source names.
- `src/Beacon.Core/Handlers/DataMigration/DeleteMigrationJobHandler.cs` — wraps the existing `IMigrationService.DeleteMigrationJob` and returns `(success, errorMessage)`.
- `src/Beacon.SampleProject/Endpoints/MigrationsEndpoints.cs` — adds `GET /beacon/api/migrations/jobs` and `DELETE /beacon/api/migrations/jobs/{id}` (`forceDelete` query flag). Keeps the existing `POST /jobs` and `GET /executions`.

All other endpoints (Dashboards CRUD, AI actors list/detail, auth/setup) already existed and the new pages call them via the generated client (`beaconApi()`).

## Auth gating change

`App.tsx` is restructured so `/login`, `/logout`, `/setup`, `/error` render at the top of the route tree, outside `<RequireAuth>`. The authenticated tree is wrapped under `<Route path="*">` so React Router lets anonymous visitors hit the landing pages without first being bounced through `RequireAuth`. `RequireAuth.tsx` now redirects unauthenticated users to `/app/login` (was `/beacon`).

The login form posts to the existing `Beacon.UI.Authentication.LoginEndpoints` (`POST /beacon/api/auth/login`) and the setup form to the existing `Beacon.UI.Authentication.SetupEndpoints` (`POST /beacon/api/setup/superadmin`). Those endpoints currently live in `Beacon.UI`; moving them to `Beacon.SampleProject` is a separate cutover task — this slot just lets the React UI talk to them where they sit today.

## Deferrals (intentional)

- **Dashboard builder** — drag-resize grid + widget-config dialogs (KPI/Chart/Table/Gauge/Mermaid) is a substantial widget editor. Ships as an informational placeholder that lists the dashboard's existing widgets and links back to the viewer. No drag-resize, no per-type config UI. Plan: separate batch.
- **Dashboard chart/table/gauge/mermaid widget rendering** — viewer currently displays widget metadata + a "Live rendering deferred" notice for non-KPI widgets. KPI widgets render their `value` from `configurationJson`. Live data fetch + chart libraries are out of scope.
- **AI actor refine flow + plan review** — Blazor has dedicated dialogs (`RefineAiActorDialog`, `ReviewAiActorPlanDialog`) wired to LLM calls; React detail page shows the actor's instructions, KPIs, and config but leaves "refine" / "review plan" / "execute" mutations for a follow-up.
- **AI actor create dialog** — list says "create in the legacy admin UI" until the create wizard ships in React.
- **Migration job edit page** — would either need (a) reusing `CreateMigrationJobDialog` in pre-loaded mode (requires an `UpdateMigrationJob` handler — `IMigrationService.UpdateMigrationJob` exists but no MediatR wrapper or endpoint yet) or (b) a dedicated edit page. Deferred until both are ready. The list lets users view, delete, and re-create jobs.
- **Migration job multi-step query builder** in `CreateMigrationJobDialog` — deferred from Batch 5e (see existing TSX comment); no new deferral here.

Each deferral leaves either a "Coming soon" page or a clearly worded sub-section explaining the gap.

## Files touched

Backend (3):
- `src/Beacon.Core/Handlers/DataMigration/GetMigrationJobsHandler.cs` (new)
- `src/Beacon.Core/Handlers/DataMigration/DeleteMigrationJobHandler.cs` (new)
- `src/Beacon.SampleProject/Endpoints/MigrationsEndpoints.cs` (added two endpoints)

Frontend — new (12):
- `src/Beacon.SampleProject/web/src/routes/dashboards/queries.ts`
- `src/Beacon.SampleProject/web/src/routes/dashboards/DashboardsListPage.tsx`
- `src/Beacon.SampleProject/web/src/routes/dashboards/DashboardViewerPage.tsx`
- `src/Beacon.SampleProject/web/src/routes/dashboards/DashboardBuilderPage.tsx`
- `src/Beacon.SampleProject/web/src/routes/ai-actors/queries.ts`
- `src/Beacon.SampleProject/web/src/routes/ai-actors/AiActorsListPage.tsx`
- `src/Beacon.SampleProject/web/src/routes/ai-actors/AiActorDetailPage.tsx`
- `src/Beacon.SampleProject/web/src/routes/migration-jobs/queries.ts`
- `src/Beacon.SampleProject/web/src/routes/migration-jobs/MigrationJobsListPage.tsx`
- `src/Beacon.SampleProject/web/src/routes/auth/LoginPage.tsx`
- `src/Beacon.SampleProject/web/src/routes/auth/LogoutPage.tsx`
- `src/Beacon.SampleProject/web/src/routes/auth/SetupPage.tsx`
- `src/Beacon.SampleProject/web/src/routes/auth/ErrorPage.tsx`

Frontend — modified (3):
- `src/Beacon.SampleProject/web/src/App.tsx` — adds 7 routes; restructures top-level so anonymous routes sit outside `<RequireAuth>`.
- `src/Beacon.SampleProject/web/src/feature-flags.ts` — appends `dashboards`, `ai-actors`, `migration-jobs` to `MIGRATED_PAGES`.
- `src/Beacon.SampleProject/web/src/auth/RequireAuth.tsx` — redirect target `/beacon` → `/app/login`.

Plus `wwwroot/app/` synced from the Vite build output.

## Verification

- `dotnet build --property WarningLevel=0` — succeeded.
- `dotnet test src/Beacon.Tests/Beacon.Tests.csproj` — 35 passed, 0 failed.
- `npm run build` — succeeded.
- `npm test -- --run` (Vitest) — 13 passed across 9 files.
- `wwwroot/app/` rebuilt from `web/dist/`; `*.dswa.cache.json` and `*.Up2Date` cleared per the React-shell asset-rot lesson.

## Cutover impact

Removing `Beacon.UI` next will require moving `LoginEndpoints.cs` and `SetupEndpoints.cs` from `src/Beacon.UI/Authentication/` to `src/Beacon.SampleProject/Endpoints/` (or splitting auth into its own project). The React pages already hit those URLs, so the move is a pure code relocation with no contract change.

The remaining Blazor pages after this batch (per Batch 7 audit): the legacy widget builder/widget configurations themselves. These are out-of-band admin tooling and do not block the cutover — they can be removed alongside `Beacon.UI` once dashboard editing migrates fully.

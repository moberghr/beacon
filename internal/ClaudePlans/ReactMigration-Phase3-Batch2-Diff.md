# React Migration — Phase 3 Batch 2 Behavioral Diff

**Date:** 2026-05-07
**Branch:** `feat/react-phase3`
**Spec:** `ClaudePlans/ReactMigration-Phase3.md` (§ Batch 2)
**Predecessor:** `ClaudePlans/ReactMigration-Phase3-Batch1-Diff.md`

## Goal achieved

Seven additional pages live under `/app/*` plus the first round of D3 backend
work (notifications, control tower, migration history) and Vitest + RTL + MSW
test infrastructure with a first passing test.

## Pages added

| Route | File | Source endpoint | Notes |
|---|---|---|---|
| `/app/projects/:id` | `routes/projects/ProjectDetailPage.tsx` | `getProjectDetail` (existing) | Tabs: Overview, Repositories, Documentation, AI Actors. Documentation + AI Actors are placeholders for Batch 4. |
| `/app/notifications` | `routes/notifications/NotificationsPage.tsx` | `getNotifications` (NEW) | Read-only list. Severity pill, recipients summary, row counts. |
| `/app/home` | `routes/home/HomePage.tsx` | `getProjects` + `getControlTowerStatistics` + `getNotifications` | KPI grid + quick-nav. **No fake data** — every value is sourced from a real handler. Tremor not used yet (the design-system KPI tiles are sufficient for the read-only home). |
| `/app/about` | `routes/about/AboutPage.tsx` | static | Capability cards, use cases, tech stack. Mermaid diagrams from the Blazor About are deferred to Batch 5/6 alongside QueryEditor (where Mermaid arrives). |
| `/app/control-tower` | `routes/control-tower/ControlTowerPage.tsx` | `getControlTowerStatistics` + `getControlTowerHealth` (NEW) | KPI grid + RAG health table. Filters/auto-refresh ship in a later batch. |
| `/app/migration-history` | `routes/migration-history/MigrationHistoryPage.tsx` | `getMigrationExecutions` (NEW) | Read-only execution log. Status pills, rows-out/rows-in, throughput. |
| `/app/queries/:id/versions` | `routes/queries/QueryVersionsPage.tsx` | `getQueryVersions` (existing, Phase 1) | Click row → version-detail placeholder until Batch 5. |
| `/app/queries/:id/versions/:versionId` | `routes/queries/QueryVersionDetailPage.tsx` | — | Placeholder (deferred to Batch 5 with QueryEditor). |

`MIGRATED_PAGES` now contains: `projects`, `home`, `about`, `notifications`,
`control-tower`, `migration-history`. `migration-history` is reachable by direct
URL only — the sidebar's `data-migration` slug still points to Blazor's job
list. Once that Blazor page ports we can fold history into the same slug.

The default landing route changed from `/app/projects` to `/app/home`.

## Backend (D3) handlers added

| Handler | Service it wraps | Endpoint | Operation ID |
|---|---|---|---|
| `src/Beacon.Core/Handlers/Notifications/GetNotificationsHandler.cs` | `INotificationService.GetQueryExecutionHistory` | `GET /beacon/api/notifications` | `GetNotifications` |
| `src/Beacon.Core/Handlers/ControlTower/GetControlTowerStatisticsHandler.cs` | `IControlTowerService.GetControlTowerStatistics` | `GET /beacon/api/control-tower/statistics` | `GetControlTowerStatistics` |
| `src/Beacon.Core/Handlers/ControlTower/GetControlTowerHealthHandler.cs` | `IControlTowerService.GetSubscriptionHealthOverview` | `GET /beacon/api/control-tower/health` | `GetControlTowerHealth` |
| `src/Beacon.Core/Handlers/DataMigration/GetMigrationExecutionsHandler.cs` | `IMigrationService.GetMigrationExecutions` | `GET /beacon/api/migrations/executions` | `GetMigrationExecutions` |

All four follow the established pattern: `internal sealed class` + primary
constructor, request/result records colocated, query-string params on the
endpoint side, `WithName(...)` so `OpenApiContractTests` recognises the binding.

The Notifications handler reshapes the service-layer
`QueryExecutionHistoryListData` into a flatter `NotificationEntry` so the React
page doesn't need to know about the `Notifications[]` substructure (it just
wants names of recipients).

ControlTower split into two handlers (statistics + health overview) because the
Blazor page already loads them independently and React consumers can fan out via
`Promise.all` in the same TanStack hook.

Migration executions handler proxies straight through — the service-layer DTO
shape is fine for read-only consumers.

## Endpoint files added

- `src/Beacon.SampleProject/Endpoints/NotificationsEndpoints.cs`
- `src/Beacon.SampleProject/Endpoints/ControlTowerEndpoints.cs`
- `src/Beacon.SampleProject/Endpoints/MigrationsEndpoints.cs`

All registered in `BeaconApiEndpoints.MapBeaconApi()`.

## Design-system additions

- `web/src/components/Tabs.tsx` — generic `<Tabs>` driven by the existing
  `.tabs` / `.tab` / `.tab__count` CSS in `styles-beacon.css`. No new CSS
  needed; the design system already had the tab styles ready.

No additions to `styles-beacon.css` itself — every component composes existing
classes (`kpi-grid`, `kpi`, `card`, `btn`, `pill--ok|warn|crit|info`, `tabs`,
`tab`, `muted`, `mono`).

## Test infrastructure

- `vitest.config.ts` + `vitest.setup.ts` (jsdom env, MSW registered globally
  with `onUnhandledRequest: 'error'` so a missed mock fails the test loud).
- `src/test/handlers.ts` — default MSW handlers. `*` origin pattern so jsdom's
  default origin (`http://localhost:3000`) doesn't break matching.
- `src/test/render.tsx` — `renderWithProviders` wraps with a fresh
  `QueryClient` (retry off, gcTime 0) + `MemoryRouter`. Cache state never leaks
  between tests.
- First test: `routes/projects/ProjectsListPage.test.tsx` — renders the
  Projects list with two MSW-mocked rows, asserts both names render and the
  "2 total" count line appears.
- `package.json` scripts: `test` (vitest run, one-shot for CI) + `test:watch`.

CI integration deferred — running `npm test` from the worktree manually for
now. A separate `npm test` step in `.github/workflows/w-build.yml` lands when
we add a second test (probably alongside the first form in Batch 3).

## OpenAPI contract test

`OpenApiContractTests.EveryMediatRHandlerIsExposedViaHttp` passes with the
four new handlers — operation IDs `GetNotifications`,
`GetControlTowerStatistics`, `GetControlTowerHealth`, `GetMigrationExecutions`
all appear in the live OpenAPI document.

## Verified

- `dotnet build Beacon.SampleProject -c Release --property WarningLevel=0` — green
- `dotnet test src/Beacon.Tests/Beacon.Tests.csproj` — 35/35 pass (no new translation tests; the new handlers wrap services rather than running new EF queries)
- `npm run build` — green; total bundle ~94 KB gzipped (first-paint), per-route chunks 0.4–5.8 KB
- `npm test` — 1/1 pass

## Behavioural diff vs Blazor

- **Project detail:** Blazor's project page nests sub-routes (Tabs, repositories management, documentation generator). React Batch 2 keeps the four-tab header but defers Documentation + AI Actors content to Batch 4 — they show explicit "ships in Batch 4" empty states, no fake data.
- **Notifications:** Blazor uses `MudDataGrid` server-side paging + status filter chip. React ships a flat 100-row read-only list — pagination/filter UI lands in Batch 4. The data shape (status enum, recipients, comment) is identical.
- **Home:** Blazor's Home is a much fuller dashboard with Top Subscriptions, recent activity feed, channel breakdown, and execution-time chart. React Batch 2 ships the eight KPI tiles + a quick-nav card — everything else needs more handlers (top-subs, activity feed) and Tremor charts that come in Batch 3+.
- **About:** Mermaid diagrams omitted. Capability + use-case + tech-stack content ported.
- **Control Tower:** Filter chips, auto-refresh switch, anomaly sparkline, and per-row drill-in are deferred. The KPI summary and the health table render the same RAG status data.
- **Migration history:** Blazor combines this with the migration-jobs page and the retry-execution action. React Batch 2 is read-only history only; the retry path lands when migration jobs port (Batch 4 or later).
- **Query versions:** Blazor's version page also has restore + diff. React Batch 2 is the listing only; clicking through goes to a placeholder.

## Out of scope (deferred)

- Mutations + forms (Batch 3 — first will be Add/Edit Project)
- Tremor chart usage (Batch 3 with first dashboard)
- Notification actions (mark-read, dismiss, status filter UI) — Batch 4
- ControlTower auto-refresh and per-row drill-in — Batch 4
- Migration retry action — Batch 4
- Query version restore + diff viewer — Batch 5
- Mermaid for About diagrams — Batch 6
- CI step for `npm test` — when the second test lands (Batch 3)

## Patterns added (reference for Batch 3+)

1. **Tabs use the generic `<Tabs>` component** (`@/components/Tabs`) with a
   typed key union for `active`. Use `<Icon.X size={13} />` inside labels to
   match the design-system size.
2. **For pages that need multiple endpoints, fold them into a single
   `useFooQuery()` that does `Promise.all` inside `queryFn`** — keeps loading
   states unified and eliminates "partial render" flicker. See
   `routes/home/queries.ts` and `routes/control-tower/queries.ts`.
3. **MSW default handlers use `*` origin** so jsdom's default origin doesn't
   need to be set in `vitest.setup.ts`. Match by path only.
4. **Pages that wrap a service via a new MediatR handler must also add the
   matching endpoint in the same commit** — `OpenApiContractTests` enforces it.
5. **Backend `BaseListRequest` fields are `Page` / `PageSize`, not `Skip` /
   `Take`.** New handlers that wrap services taking `BaseListRequest` must use
   the Page/PageSize shape on the request record too.

## Phase 3 progress

- [x] Batch 1 — Foundation + Projects reference page
- [x] Batch 2 — Read-only pages + project detail + Vitest infra
- [ ] Batch 3 — Simple CRUD
- [ ] Batch 4 — Medium pages
- [ ] Batch 5 — Heavy 6 (one per slot)
- [ ] Batch 6 — Specialty
- [ ] Batch 7 — Cutover (delete Blazor + Beacon.UI)

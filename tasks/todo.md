# Todo — React Migration Phase 3 Batch 2

**Spec:** `ClaudePlans/ReactMigration-Phase3.md` (Batch 2 section)
**Predecessor:** `ClaudePlans/ReactMigration-Phase3-Batch1-Diff.md`
**Branch:** `feat/react-phase3` (continue — push to existing draft PR #13)
**Worktree:** `/Users/mirkobudimir/Dev/MiBu/semantico-react`

---

## Session entry point

Open the worktree and confirm:

```bash
cd /Users/mirkobudimir/Dev/MiBu/semantico-react
git log --oneline -3   # top should mention "phase 3 batch 1 — foundation"
git status --short     # tasks/todo.md may be modified, ignored
```

Read `ClaudePlans/ReactMigration-Phase3-Batch1-Diff.md` first — it's the contract for what patterns Batch 2 must follow. Then `ClaudePlans/ReactMigration-Phase3.md` § "Batch 2 — Read-only pages + project detail (~4 days)".

**Critical constraints (from Batch 1):**
- **NO Tailwind utility classes, NO shadcn primitives.** Use the Beacon-design system in `web/src/styles-beacon.css`. Add new CSS to `styles-beacon.css` if a needed pattern is missing.
- All routes lazy-loaded. One file per page in `routes/<area>/`.
- Tables via `<DataTable>`. Icons via `Icon.Xxx` (add to `Icon.tsx` rather than installing lucide).
- Format helpers always via `lib/format.ts`.
- API via `beaconApi()` singleton + TanStack Query hooks in `routes/<area>/queries.ts`.
- Add migrated route slug to `web/src/feature-flags.ts` `MIGRATED_PAGES` after each page goes live.

---

## Batch 2 pages to ship

### 2.1 — Project detail (`/app/projects/:id`)
- [x] Replace `routes/projects/ProjectDetailPage.tsx` placeholder with the real page
- [x] `useProjectDetailQuery(id)` calling `beaconApi().getProjectDetail(id)`
- [x] Use design-system tabs (look in `Beacon-design/query-detail.jsx` for the pattern)
- [x] Tabs: Overview, Repositories, Documentation (placeholder until Batch 4), AI Actors (placeholder until Batch 4)
- [x] Verify: `/app/projects/{id}` loads, shows project, tab navigation works

### 2.2 — Notifications list (`/app/notifications`)
**FIRST D3 WORK** — Notifications has no MediatR handler today; Blazor calls `INotificationService` directly. Per spec D3, add MediatR handlers + endpoints first.

- [x] Add `Beacon.Core/Handlers/Notifications/GetNotificationsQuery.cs` + handler. Pattern: see `Beacon.Core/Handlers/Projects/GetProjectsHandler.cs`. Wraps `INotificationService.GetNotificationsAsync(...)`.
- [x] Add HTTP endpoint in `Beacon.SampleProject/Endpoints/NotificationsEndpoints.cs` (new file, registered in `BeaconApiEndpoints.cs`). `WithName("GetNotifications")` so the OpenAPI contract test passes.
- [x] Run `npm run codegen` against the running app to regenerate `web/src/api/generated/beacon-api.ts`
- [x] Add Notifications page: list with severity, message, timestamp, status. Read-only (actions deferred to Batch 4).
- [x] Add to `MIGRATED_PAGES`
- [x] Verify: `dotnet test` passes (contract test should pass automatically once endpoint exists), `/app/notifications` loads

### 2.3 — Home page with Tremor widgets (`/app/home`)
- [x] Install `@tremor/react` is already done; first usage here
- [x] Read `Beacon-design/dashboard.jsx` for the KPI grid + chart patterns. **Match it** — same `kpi-grid` + KPI cards. The design has its own charting; Tremor is the React equivalent.
- [x] Hooks: `useDashboardsQuery()` (already wrapped in Phase 1) for any aggregate stats
- [x] If no aggregate dashboard endpoint exists yet, build the page with placeholder values from real data sources (per §9.1: NEVER fake/seed data)
- [x] Add to `MIGRATED_PAGES` as `home`
- [x] Verify: `/app/home` renders KPIs without fake data

### 2.4 — About page (`/app/about`)
- [x] Trivial port. Static content; whatever Blazor's About.razor shows.
- [x] Add to `MIGRATED_PAGES`

### 2.5 — ControlTower (`/app/control-tower`)
- [x] Read Blazor's `ControlTower.razor` to understand what data it shows. May need new MediatR handlers if it uses services directly.
- [x] If new handlers needed, follow same D3 pattern as Notifications (add handler + endpoint + regen TS).
- [x] Add to `MIGRATED_PAGES`

### 2.6 — Migration history (`/app/migration-history`)
- [x] Existing endpoint: check Phase 1 endpoints for migration jobs.
- [x] Read-only list. Add to `MIGRATED_PAGES`.

### 2.7 — Query version history (`/app/queries/:id/versions`)
- [x] Phase 1 wrapped this — endpoint is `/beacon/api/queries/{id}/versions`.
- [x] Read-only list. Click version → version detail (placeholder until Batch 5).

---

## Test infrastructure (do this once during 2.1 or 2.2)

- [x] Set up Vitest + React Testing Library + MSW
  - `npm install -D vitest @testing-library/react @testing-library/jest-dom jsdom msw`
  - Add `vitest.config.ts`, `vitest.setup.ts`
  - Add `npm test` script
- [x] First test: `routes/projects/ProjectsListPage.test.tsx` — render with MSW-mocked `/beacon/api/projects`, assert table shows rows
- [x] CI: figure out how to run vitest in `dotnet test` flow OR add a separate npm step to `.github/workflows/`. For now, just running it manually in the worktree is acceptable.

---

## Acceptance gate

- [x] `dotnet build -c Release --property WarningLevel=0` green
- [x] `dotnet test` — all tests pass (35 + any new translation tests for new handlers + OpenAPI contract still passes)
- [x] `npm run build` green
- [x] `npm test` (vitest) green if set up
- [ ] Manual browser smoke per migrated page
- [x] `ClaudePlans/ReactMigration-Phase3-Batch2-Diff.md` written
- [ ] Commit + push to `feat/react-phase3`, PR #13 stays draft

---

## Out of scope (deferred to later batches)

- Mutations (Add project, edit project, delete project) — Batch 3
- Notifications actions (mark read, dismiss) — Batch 4
- Subscriptions / Tasks / Recipients pages — Batch 3
- Heavy 6 (QueryEditor etc.) — Batch 5

---

## Pending verification from Batch 1 (do FIRST in fresh session)

The user may not have browser-loaded `/app/projects` yet. **Verify before adding new pages:**
1. Run `dotnet run --project Beacon.SampleProject -c Release` (or have user do it)
2. Open `http://localhost:5296/app/projects` (or 7187/app/projects on https) while logged in
3. Confirm: project list renders, design-system styling matches Blazor, sidebar nav shows other links going to `/beacon/...`
4. If it doesn't work, fix that BEFORE starting any Batch 2 page

---

## Subagent caveat

In the previous session, subagent Bash was denied by the sandbox. If you want to run pieces of Batch 2 in parallel via subagent, that constraint may still hold — fall back to inline execution if the subagent reports it can't run shell.

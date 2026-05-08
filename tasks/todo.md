# Todo — React Migration Phase 3 Batch 5a (QueryDetails)

**Spec:** `ClaudePlans/ReactMigration-Phase3.md` (Batch 5a, line 166)
**Predecessor diffs:** Batches 1–4
**Branch:** `feat/react-phase3` (continue — push to draft PR #13)
**Worktree:** `/Users/mirkobudimir/Dev/MiBu/semantico-react`
**Slot rule (spec line 164):** Heavy pages get their own slot — only QueryDetails this round.

---

## Source page
`Beacon.UI/Components/Pages/Queries/QueryDetails.razor` — **865 LOC** (spec quoted ~275; reality is heavier). Tabs: Overview, Recipients, Subscriptions, Anomaly. Plus version history pane + change history pane (the existing `Beacon.UI/Components/Pages/Queries/QueryVersionHistory.razor` and `QueryChangeHistoryPanel.razor` likely embed here).

## Existing back-end (do NOT re-implement)
- `Beacon.Core/Handlers/Queries/`: `ToggleQueryLockHandler`, `GetQueryChangeHistoryHandler`
- `Beacon.Core/Handlers/QueryVersions/`: `GetQueryVersions`, `GetQueryVersionDetail`, `DiffQueryVersions`, `RestoreQueryVersion`
- React Phase 1 already wraps versions list (Batch 2 ships `/app/queries/:id/versions`)

---

## Critical constraints (carry over)
- NO Tailwind, NO shadcn primitives. Beacon-design CSS in `web/src/styles-beacon.css`.
- NO fake/seed/demo data.
- All routes lazy-loaded.
- Tables → `<DataTable>`; icons → `Icon.Xxx`; formatters → `lib/format.ts`.
- API → `beaconApi()` + TanStack Query hooks.
- In-app `<Link>`/`navigate()` paths must NOT include `/app/`.
- After React-only build, sync `web/dist/` → `wwwroot/app/` (or `dotnet build` triggers it).
- Forms: RHF + Zod 4 (`z.email()`, NOT `z.string().email()`).
- Mutations: `useMutation` invalidate on success, toast.success/error from RFC 7807.
- Backend: MediatR `internal sealed class` + primary ctor, `IDbContextFactory<BeaconContext>`, `.Select(new ...)` no `.Include()`, throw `InvalidOperationException` (no Result pattern), lambda `x`, LINQ on separate lines, `CancellationToken` everywhere.
- New endpoints must have `.WithName(...)` matching the request type so the OpenAPI contract test passes (lesson from Batch 3 — `GetTasksRequestQuery` failed; `GetTasksQuery` passed).
- Version detail dialog uses existing `Beacon.UI/Components/Pages/Queries/QueryVersionDetailDialog.razor` as reference.

---

## Pages to ship

### 5a — `/app/queries/:id` Query detail

Replace placeholder `QueryVersionDetailPage` chain with full QueryDetails port.

**Route map for this slot:**
- `/app/queries/:id` — main detail (NEW page replacing the 2.7 placeholder)
- `/app/queries/:id/versions` — already shipped Batch 2 (keep)
- `/app/queries/:id/versions/:versionId` — already shipped Batch 2 (keep, but make sure version diff dialog works)

**Tabs:**
- [ ] **Overview** — query name, description, target data source, schedule (cron), parameters, last execution status, anomaly count summary, lock status with toggle (uses existing `ToggleQueryLock`).
- [ ] **Recipients** — list recipients attached to the query. CRUD via existing `Beacon.Core/Handlers/Recipients/` (Batch 3) — but query-scoped; may need new `GetQueryRecipients`, `AttachRecipientToQuery`, `DetachRecipientFromQuery` handlers. Audit Blazor first.
- [ ] **Subscriptions** — list subscriptions for the query. Existing `GetSubscriptionsQuery` (Batch 4) may filter by query — verify. If not, add filter parameter or new handler.
- [ ] **Anomaly** — anomaly stream / detection settings. Likely D3 work: handlers for `GetQueryAnomalies`, `UpdateQueryAnomalySettings`. Audit Blazor for the actual shape.

**Side panels:**
- [ ] **Version history** — embedded pane (NOT a separate page). Reuse `useQueryVersionsQuery` from Batch 2. Click a version → `QueryVersionDetailDialog.tsx` (NEW — RHF NOT needed; read-only diff).
- [ ] **Change history** — pane using `GetQueryChangeHistory` (existing handler).

**Actions:**
- [ ] **Lock/unlock** — toggle button using existing `ToggleQueryLock`.
- [ ] **Restore version** — button on a version row, calls existing `RestoreQueryVersion`. Confirm via `ConfirmDialog`.
- [ ] **Run query** (if Blazor exposes it) — likely calls existing run service. May be deferred to QueryEditor (5f).

**Out-of-scope here** (deferred to later 5x slots):
- Inline query editing (Monaco) — that's 5f QueryEditor
- Multi-step query stepper — 5f
- Add subscription multi-step dialog — 5c
- Add recipients (multi-select) dialog — 5c

---

## Audit pass (do FIRST)
1. Read `Beacon.UI/Components/Pages/Queries/QueryDetails.razor` end-to-end. List every `@inject`, every dialog opened, every method called.
2. Compare against existing handlers. Tabulate: (a) handlers we have, (b) handlers we need to add, (c) functionality we defer.
3. Record the audit at the top of the diff doc.

## D3 backend work (estimated)
Likely new handlers (audit confirms):
- `Beacon.Core/Handlers/Queries/GetQueryDetailHandler.cs` — full query metadata for the page
- `Beacon.Core/Handlers/Queries/GetQueryRecipientsHandler.cs`
- `Beacon.Core/Handlers/Queries/AttachQueryRecipientHandler.cs`
- `Beacon.Core/Handlers/Queries/DetachQueryRecipientHandler.cs`
- `Beacon.Core/Handlers/Queries/GetQueryAnomaliesHandler.cs`
- `Beacon.Core/Handlers/Queries/UpdateQueryAnomalySettingsHandler.cs` (if Blazor edits them)
- (Plus any execution-history pane handlers if not 5f scope)

Endpoint additions go in existing `Beacon.SampleProject/Endpoints/QueriesEndpoints.cs`. No new file unless area genuinely diverges.

## Frontend work
- `routes/queries/QueryDetailPage.tsx` (NEW — replaces the 2.7 placeholder routing)
- `routes/queries/tabs/{OverviewTab,RecipientsTab,SubscriptionsTab,AnomalyTab}.tsx` (sub-files OK because the page is large; keep file-per-component small)
- `routes/queries/QueryVersionDetailDialog.tsx`
- `routes/queries/AttachRecipientDialog.tsx` (if needed)
- `routes/queries/queries.ts` — extend with new hooks
- Update `App.tsx` lazy-route registration: `/queries/:id` → new page

## Cross-cutting
- [ ] Translation tests for any non-trivial new EF query (per §4.6) — guard against Npgsql translation failures.
- [ ] At least one new Vitest test (e.g. RecipientsTab attach flow).

---

## Acceptance gate
- [ ] Audit table at top of Batch5a diff.
- [ ] `dotnet build -c Release --property WarningLevel=0` green.
- [ ] `dotnet test` green; OpenAPI contract still passes for ALL handlers.
- [ ] `npm run build` green.
- [ ] `npm test` green.
- [ ] Manual browser smoke: `/app/queries/:id` loads, all 4 tabs render, version pane works, lock toggle works.
- [ ] `ClaudePlans/ReactMigration-Phase3-Batch5a-Diff.md` written.
- [ ] `git commit` — orchestrator commits if subagent's git is sandboxed.

---

## Out of scope (later 5x slots)
- 5b SubscriptionDetails
- 5c AddSubscription / AddRecipients multi-step dialogs
- 5d AddDataSourceDialog (heavy multi-engine form)
- 5e CreateMigrationJob wizard
- 5f QueryEditor + Monaco

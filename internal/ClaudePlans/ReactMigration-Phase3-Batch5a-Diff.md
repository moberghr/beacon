# Phase 3 Batch 5a — QueryDetails (backend-only slice)

**Branch:** `feat/react-phase3` (PR #13 draft)
**Slot rule:** Heavy pages get their own slot. This commit ships the backend D3 only. Frontend port deferred to a fresh session.

---

## Why this is backend-only

The subagent dispatch for 5a hit a sandbox-wide Bash denial and returned with no work done. To preserve session momentum, the orchestrator did the audit + backend D3 inline and deferred the frontend tabs to the next session, where a subagent can resume.

---

## Audit (mandatory per slot rules)

Source page: `src/Beacon.UI/Components/Pages/Queries/QueryDetails.razor` — **865 LOC**, contradicting the spec's quoted "275 LOC".

| What Blazor uses | Existing handler/endpoint? | Action |
|---|---|---|
| `IQueryService.GetQueryDetails(id, ct)` | NO MediatR wrapper | **D3 — added `GetQueryDetailHandler`** |
| `IQueryExecutionPreviewService.ExecuteQueryPreview(id, ct)` | direct service call | DEFER → 5f QueryEditor (execution flow) |
| `IQueryExecutionPreviewService.ExecuteStepPreview(id, stepOrder, params, ct)` | direct service call | DEFER → 5f |
| `Mediator.Send(new GetPendingApprovalsQuery {QueryId = Id})` | EXISTS | reuse (frontend) |
| `IBeaconAuthorizationProvider.HasWritePermissionAsync()` | auth | reuse on frontend via useAuth |
| `Mediator.Send(new ToggleQueryLockCommand {...})` | EXISTS (Batch ≤2) | reuse |
| `Mediator.Send(new GetQueryChangeHistoryQuery {...})` | EXISTS | reuse |
| Subscriptions on the query | included in `QueryDetailsData.Subscriptions` | reuse |
| Notification statistics chart | included in `QueryDetailsData.NotificationHistory` | reuse |
| Execution time chart | included in `QueryDetailsData.ExecutionTimeHistory` | reuse |
| Steps + parameters | included in `QueryDetailsData.Steps` | reuse |
| `ExecuteStepParametersDialog` (MudBlazor) | UI dialog | DEFER → 5f QueryEditor parameter dialog |
| `AddSubscription` button → `subscriptions/add/{id}` | nav | frontend builds link to `/subscriptions` (existing) |

**Speculation in the original todo that the audit ruled out:**
- "Anomaly tab" — does not exist in `QueryDetails.razor`. Removed from scope.
- "Recipients tab needs `GetQueryRecipients` handler" — recipients are subscription-scoped per Blazor: "Add recipients via each subscription." No new handler needed; the React Recipients view will list subscriptions and link out.
- "AttachQueryRecipient / DetachQueryRecipient handlers" — same reasoning, not needed.

---

## Backend D3 added

### `src/Beacon.Core/Handlers/Queries/GetQueryDetailHandler.cs`
- `internal sealed class` + primary constructor
- Delegates to `IQueryService.GetQueryDetails(id, ct)` (which already returns the rich `QueryDetailsData` shape consumed by the entire React detail page including tabs, KPIs, charts, subscription list, version pane data plumbing).
- Throws `InvalidOperationException` when the query is not found (per §2.9, §9.8).
- Result type is the existing `Beacon.Core.Services.QueryDetailsData` — no DTO duplication.

```csharp
internal sealed class GetQueryDetailHandler(IQueryService queryService)
    : IRequestHandler<GetQueryDetailQuery, QueryDetailsData>
{ ... }

public record GetQueryDetailQuery : IRequest<QueryDetailsData>
{
    public required int QueryId { get; init; }
}
```

### `src/Beacon.SampleProject/Endpoints/QueriesEndpoints.cs`
- New endpoint: `GET /beacon/api/queries/{id:int}`.
- `.WithName("GetQueryDetail")` so the OpenAPI contract test (`src/Beacon.Tests/Integration/Api/OpenApiContractTests.cs`) matches `GetQueryDetailQuery` after suffix strip.
- `.RequireAuthorization()` is inherited from the parent `MapBeaconApi` group.

---

## Acceptance gate

| Gate | Result |
|---|---|
| `dotnet build -c Release --property WarningLevel=0` | green |
| `dotnet test` | 35/35 (OpenAPI contract still passes) |
| `npm run build` | green (triggered by .NET build target) |
| `npm test` | not run this session — no frontend changes |

## Files touched

- `src/Beacon.Core/Handlers/Queries/GetQueryDetailHandler.cs` (NEW)
- `src/Beacon.SampleProject/Endpoints/QueriesEndpoints.cs` (added GET endpoint)
- `tasks/todo.md` (marked Step 1 audit + D3 portion done; frontend items remain unchecked)
- `ClaudePlans/ReactMigration-Phase3-Batch5a-Diff.md` (this file)

## Stale-cache lesson (worth a memory)

The first build failed with `No file exists for the asset 'AboutPage-Bs2L0wO1.js'` even though the file existed on disk. The issue is the .NET static-web-assets DSWA cache: `obj/Release/net9.0/{rbcswa,rpswa,...}.dswa.cache.json` plus the `*.Up2Date` markers retain stale chunk-hash references when `wwwroot/app/` is regenerated outside the build (e.g. via `rsync` or by a prior build aborted mid-`StageReactBuild`). Symptom: build error names a hash that doesn't match current dist/wwwroot but the message claims the file is missing in BOTH the absolute path AND the relative path.

**Fix:** delete `obj/**/*.dswa.cache.json` and `obj/**/*.Up2Date`, then rebuild. Do NOT delete the entire obj — that triggers a slow restore.

## Deferred to next session (frontend)

- `routes/queries/QueryDetailPage.tsx` (replaces 2.7 placeholder routing)
- `routes/queries/tabs/{OverviewTab,RecipientsTab,SubscriptionsTab}.tsx`
- `routes/queries/QueryVersionDetailDialog.tsx`
- Embedded ChangeHistoryPane + VersionHistoryPane
- Lock toggle button + RestoreVersion confirmation
- Vitest test for one interaction
- App.tsx route registration
- `npm run codegen` to fold the new endpoint into the typed client (optional — fetchJson<T> wrapper works in the meantime)

---

## Frontend follow-up — shipped 2026-05-08

`/queries/:id` is now wired in the React shell, mirroring the Beacon-design
`query-detail.jsx` page-hero / KPI / two-column / side-rail / save-bar
shape used on TaskDetailPage.

### Components

- `routes/queries/QueryDetailPage.tsx` — assembly point, lazy-routed at
  `/queries/:id` in `App.tsx`. Loading / error / invalid-id branches mirror
  TaskDetailPage. Cmd+Enter / Ctrl+Enter keyboard shortcut routes to the
  legacy `/beacon/queries/{id}` execution screen.
- `routes/queries/parts/QueryHero.tsx` — signal hero with eyebrow pills
  (status, single-step / multi-step, AI-actor / user-defined, locked).
- `routes/queries/parts/QueryKpiGrid.tsx` — 4 KPIs (Executions, Results
  rate, Subscriptions, Steps). Results rate derived from
  `notificationHistory[].successRate`.
- `routes/queries/parts/QueryPerfRow.tsx` — Avg / Fastest / Slowest
  exec time. "—" until samples exist.
- `routes/queries/parts/QueryInfoCard.tsx` — KV grid for ID, type,
  data sources, cross-DS / cross-DB flags, owner, lock state.
- `routes/queries/parts/QueryStepsCard.tsx` — read-only step list (SQL
  block + parameter chips). All editing affordances point at the legacy
  Blazor editor until 5f.
- `routes/queries/parts/FinalQueryCard.tsx` — read-only FinalQuery code
  block, only rendered when `finalQuery` is set (multi-step queries).
- `routes/queries/parts/QueryTabsCard.tsx` — Subscriptions / Executions /
  Versions / Change history tabs.
- `routes/queries/parts/SubscriptionsTab.tsx` — DataTable of
  `query.subscriptions[]`, click → `/subscriptions/{id}` (5b page).
- `routes/queries/parts/ExecutionsTab.tsx` — per-day rollup merging
  `notificationHistory` + `executionTimeHistory` keyed by ISO date.
- `routes/queries/parts/VersionsTab.tsx` — embedded version history,
  reuses `useQueryVersionsQuery`. Click → `/queries/{id}/versions/{id}`
  legacy detail page.
- `routes/queries/parts/ChangeHistoryTab.tsx` — uses new
  `useQueryChangeHistoryQuery`; renders changedAt / step / source /
  author / reason.
- `routes/queries/parts/RightRail.tsx` — heuristic next-steps card,
  people & sources card, source context checks, keyboard tip callout.
- `routes/queries/parts/QuerySaveBar.tsx` — sticky save-bar, lock /
  unlock toggle wired to `useToggleQueryLock`, Edit-SQL + Execute
  buttons routing to the legacy editor.

### Hooks (in `routes/queries/queries.ts`)

- `useQueryDetailQuery(id)` — `GET /beacon/api/queries/{id}`, returns
  `QueryDetail` shape mirroring `QueryDetailsData`.
- `useToggleQueryLock(id)` — `POST /beacon/api/queries/{id}/lock`,
  invalidates detail + list queries on success, surfaces 7807 errors
  via `toast.error`.
- `useQueryChangeHistoryQuery(id, filters)` — `GET …/change-history`
  with `stepId` / `changeSource` / `fromDate` / `toDate` /
  `maxResults` filters.
- `CHANGE_SOURCE_LABEL` map matching `Beacon.Core.Data.Enums.ChangeSource`
  (User=1, AiActor=2, Import=3).
- `useQueryVersionsQuery` retained from Batch 2.

### CSS

No new classes added — `styles-beacon.css` already covered every class
the design needed (page-hero, kpi-grid, row--3-up, q-layout, q-aside,
q-section, tabs, tab__count, save-bar, kbd, callout, person-row,
next-step, checks, step-card, params, sql__pre, etc.).

### Tests

- `routes/queries/QueryDetailPage.test.tsx` — MSW-mocks `/queries/42`,
  `/queries/42/versions`, `/auth/me`. Asserts hero name renders, KPI
  label "Executions" + value 12 are shown, lock button is rendered,
  step SQL is visible. Vitest count: 5 → 6.

### Routing

- New: `/queries/:id` → `QueryDetailPage` (lazy).
- Retained: `/queries/:id/versions` and `/queries/:id/versions/:versionId`
  (Batch 2 legacy). The new VersionsTab still navigates to the legacy
  version-detail page; rolling that into a dialog stays out of 5a.

### Out of scope (deferred to 5f QueryEditor)

- Inline step CRUD (add / edit / delete step).
- Live SQL editor (Monaco, parameter detection, autocomplete).
- Execute-step / preview-step buttons (currently link to legacy Blazor).
- Drag-reorder of steps.
- Query flow diagram (the `flow__node` chrome from `query-detail.jsx`).
- Read-only diff dialog inside the Versions tab (still routes to the
  legacy version detail page).

### Build & test status

- `dotnet build Beacon.SampleProject -c Release` — green.
- `dotnet test Beacon.Tests` — 35 passed.
- `npm run build` — green; `wwwroot/app/` synced via `rsync -a --delete`.
- `npm test` — 6 passed.

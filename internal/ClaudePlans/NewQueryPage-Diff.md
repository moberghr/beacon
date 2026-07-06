# /queries/new — full create-query form

Phase 3 batch 5g. React port of Beacon-design `add-query.jsx`.

## Scope shipped

- New page `web/src/routes/queries/NewQueryPage.tsx` (lazy-routed at `/queries/new`).
- `App.tsx` route added before `/queries/:id`.
- `QueriesListPage.tsx` header button is now `<Link to="/queries/new">`; the inline `CreateQueryDialog` was removed.
- Smoke test `NewQueryPage.test.tsx` (renders, Save disabled until name, +Add step works).

## UI structure (functional parity with the design)

- Page hero (signal variant): `Queries / NEW · DRAFT` eyebrow, *"Compose a cross-source query."*, Back / Run / Save query.
- 2-column `q-layout`:
  - **Left** — Query details card (name + description) → Cross-data-source query builder card (multi-step list using existing `SqlEditor`) → Query flow card (visual `STEP n → @resultN → +`).
  - **Right rail** — Query info card (steps / sources / parameters / cost / last edited) → Pre-flight checks card (live: name required, description required, all-steps-have-SQL, each-step-has-source, run-after-save) → Tip callout.
- Sticky `save-bar` at the bottom with DRAFT pill, kbd hints, Run + Save buttons.
- Each step has: step name, target data source dropdown (real `useDataSourcesQuery`), Monaco SQL editor, parameter chips (auto-detected from `{name}` regex with manual Re-scan and + add parameter).

## Save flow — two-step

Two-step (POST then PUT). Backend was **not** modified — reuses the existing `CreateQueryCommand` (name + description) and `UpdateQueryCommand` (full `QueryData`). The page extends the legacy `CreateQueryCommand` only conceptually; on the wire we still hit the same two endpoints.

1. `POST /beacon/api/queries` with `{ name, description }` → `{ queryId }`.
2. If the user actually authored content (any step has SQL): `PUT /beacon/api/queries/{id}` with the full payload (`queryId`, name, description, `steps[]` with `parameters`, `finalQuery: null`, `finalQueryDataSourceId: null`).
3. Navigate to `/queries/{id}` (the detail page).

If step 2 fails the new query exists as a placeholder, surfaced through the existing toast on `useUpdateQueryMutation`. The user lands on the page still, can retry. We did **not** auto-redirect to the editor on failure — the toast lives on the New page until they navigate manually.

Rationale: a single-POST handler change would require editing the recently-shipped `CreateQueryHandler` to accept a full `QueryData` payload, which collides with the existing `IQueryService.CreateQuery(QueryData)` flow that returns `BaseResponse` without an id. The two-step path reuses already-tested code paths and the user-visible result is identical.

## Run

Disabled — the preview endpoint requires a saved id. The button shows tooltip *"Save first to run"*. After Save, the user lands on `/queries/{id}` where Run is available. Documented inline in the JSDoc on the page component.

## Cmd shortcuts

- `⌘S` / `Ctrl+S` → Save (calls `onSave`, prevents browser save dialog).
- `⌘↵` reserved for Run; not wired here (Run disabled until after save).

## Pre-flight checks (live)

| Check | Tone source |
|---|---|
| Query name is required | `name.trim().length > 0` → ok / warn |
| Description is required | `description.trim().length > 0` |
| All steps have SQL | every step `sqlValue.trim().length > 0` |
| Each step has a target source | every step `dataSourceId !== 0` |
| Run to validate output | always pending — available after save |

## CSS

Zero new CSS. All classes (`q-layout`, `q-section`, `q-aside`, `q-meta-grid`, `step-card`, `step-num`, `step-name`, `step-row`, `params__head`, `param-chip`, `param-chip__type`, `flow`, `flow__node`, `flow__node--db`, `flow__node--result`, `flow__node-title`, `flow__node-sub`, `flow__edge`, `flow__plus`, `save-bar`, `kbd`, `checks`, `check__icon`, `callout`, `page-hero*`) were already vendored in `styles-beacon.css` from earlier batches.

## Deferred (vs design)

Documented inline in the page UI (save-bar hint shows *"Database explorer ships in a follow-up."*):

- Database explorer side panel inside the SQL card (schema/table tree).
- Live ping/health indicator + `38ms · ro replica` next to the data-source select.
- Diagram / JSON segmented control on the Query flow card (Diagram-only for now).
- Variables button (top-right of builder card).
- Auto-save / "auto-saved 3s ago" — page is explicit-save only.
- Step duplicate / "more" menu in the step card head.
- Drag-reorder steps. (QueryEditorPage also defers this.)
- Estimated cost preview.

## Files touched

- `src/Beacon.SampleProject/web/src/routes/queries/NewQueryPage.tsx` *(new — 599 LOC)*
- `src/Beacon.SampleProject/web/src/routes/queries/NewQueryPage.test.tsx` *(new)*
- `src/Beacon.SampleProject/web/src/routes/queries/QueriesListPage.tsx` *(button → Link, dialog removed; -77 LOC)*
- `src/Beacon.SampleProject/web/src/App.tsx` *(lazy + route added)*
- `src/Beacon.SampleProject/wwwroot/app/**` *(rsync from `web/dist` after build)*

No backend C# changes. No new EF migration.

## Verification

- `dotnet build`: green.
- `dotnet test`: 35/35 passed.
- `npm run build`: green.
- `npm test`: 14/14 passed (10 test files), including the new `NewQueryPage` smoke test.

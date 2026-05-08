# Phase 3 Batch 5f — Query Editor (Monaco)

> Worktree: `semantico-react` · Branch: `feat/react-phase3` · Parent: `3ab518e`

## Goal

Replace the legacy Blazor `QueryStepBuilder` host with a React multi-step
query editor at `/queries/:id/edit`. Includes Monaco SQL, parameter detection,
per-step preview, whole-query preview, and save (PUT). Phase 3 Batch 5a
deferred the editor to this slot; the read-only `QueryDetailPage` step list
now links here instead of `/beacon/queries/:id`.

## Backend D3

Three new MediatR slices wrapping existing services. All follow the
`internal sealed class` + primary-ctor convention; all are exposed under
`/beacon/api/queries/*` so `OpenApiContractTests` stays green.

| Handler                            | Endpoint                                                   | WithName              | Notes |
|------------------------------------|------------------------------------------------------------|-----------------------|-------|
| `UpdateQueryCommand`               | `PUT /beacon/api/queries/{id}`                             | `UpdateQuery`         | Wraps `IQueryService.UpdateQuery(QueryData)`. Body is the full `QueryData`. Throws `InvalidOperationException` on `BaseResponse.Success == false`. |
| `ExecuteQueryPreviewCommand`       | `POST /beacon/api/queries/{id}/preview`                    | `ExecuteQueryPreview` | Wraps `IQueryExecutionPreviewService.ExecuteQueryPreview`. Returns `QueryExecutionResult`. |
| `ExecuteStepPreviewCommand`        | `POST /beacon/api/queries/{id}/steps/{stepOrder}/preview`  | `ExecuteStepPreview`  | Wraps `IQueryExecutionPreviewService.ExecuteStepPreview` (parameter overload). Body: `{ parameters: ParameterValue[] | null }`. |

Files:
- `Beacon.Core/Handlers/Queries/UpdateQueryHandler.cs` (new)
- `Beacon.Core/Handlers/Queries/ExecuteQueryPreviewHandler.cs` (new)
- `Beacon.Core/Handlers/Queries/ExecuteStepPreviewHandler.cs` (new)
- `Beacon.SampleProject/Endpoints/QueriesEndpoints.cs` (added 3 routes + `ExecuteStepPreviewRequest` record)

No new migrations — purely service wrappers.

## Frontend

### New page
`Beacon.SampleProject/web/src/routes/queries/QueryEditorPage.tsx`

- Lazy-routed at `/queries/:id/edit`
- Header (name, description, Save, Run, Cancel)
- Per-step card: name, data-source select, Monaco SQL, parameter chips
- `{name}` regex auto-detects parameters on edit (mirrors Blazor logic)
- Up/down reorder buttons (drag-reorder deferred)
- Add/delete step
- Per-step Run preview opens `StepParameterDialog` if the step has params
- Whole-query Run hits `/queries/{id}/preview`
- Save invalidates `['query', id]` and `['queries', id, 'versions']`
- Read-only `Final query` block when present (editing deferred)

### New supporting files
- `web/src/components/ui/SqlEditor.tsx` — `React.lazy` wrapper around
  `@monaco-editor/react` so Monaco only loads on this page.
- `web/src/routes/queries/parts/StepParameterDialog.tsx` — RHF + Zod 4 dialog
  for parameter values. Type-aware input (`number` / `datetime-local` / `text`).
- `web/src/routes/queries/parts/PreviewResultsCard.tsx` — generic results table
  with truncation and error states.

### `queries.ts` additions
- `PARAMETER_TYPE` / `PARAMETER_TYPE_LABEL` constants (1=Number 2=DateTime 3=String).
- `UpdateQueryPayload` / `UpdateQueryStepPayload` / `UpdateQueryResult` types.
- `useUpdateQueryMutation` (PUT), `usePreviewStepMutation` (POST),
  `usePreviewQueryMutation` (POST). All RFC-7807-aware via `describeError`.

### Existing page updates
- `routes/queries/QueryDetailPage.tsx` — `legacyEditHref` now points to the
  React `editHref = /queries/:id/edit`. Execute still goes legacy.
- `routes/queries/parts/QueryStepsCard.tsx` — prop renamed `legacyEditHref` →
  `editHref`; uses `<Link>` (no `/app/` prefix).
- `App.tsx` — adds the new `/queries/:id/edit` route + lazy import.

### npm
Added `@monaco-editor/react` (`^4.7.0`).

## Tests

- `web/src/routes/queries/QueryEditorPage.test.tsx` — vitest smoke test that
  renders the editor with a stubbed Monaco (`vi.mock('@monaco-editor/react')`),
  asserts the existing step's SQL renders, and exercises "Add step".
- All 12 frontend tests pass.
- All 35 backend tests pass, including `OpenApiContractTests` (every new
  handler has a matching endpoint).

## Build / sync

- `dotnet build --property WarningLevel=0` — green.
- `npm run build` — green; `QueryEditorPage` chunk is 14.7 KB; `@monaco-editor/react`
  loads Monaco itself from CDN at runtime (default loader).
- `web/dist/` synced to `wwwroot/app/`; cleared `obj/Debug/net9.0/staticwebassets.build.json`
  and `*.dswa.cache.json` to avoid the stale-cache trap (per memory).

## Deferred — to follow-up batches

| Item | Reason |
|------|--------|
| `DatabaseExplorer` side panel + table/column drag-insert | Big surface, low priority — users can paste table names. |
| Monaco SQL completion provider with metadata | Requires the JS interop bridge `beaconSqlEditor.registerSqlCompletionProvider`; Monaco-native completion provider for React is a non-trivial port. |
| `QueryFlowDiagram` visualization | Mermaid-based; isolated component, deferrable. |
| Final-query stage editing (separate Monaco for `@result1` joins) | UI-heavy; current page shows it read-only when present. |
| Drag-reorder of steps | Up/down buttons cover the same intent. |
| Engine-specific editors (`ApiQueryEditor`, `CloudWatchQueryEditor`) | Each is a separate page-sized port; legacy link kept for now. |

## Wire-shape audit

- `QueryStep.dataSourceType` is encoded as `int` (0=Database et al.) on the
  detail wire today, even though the data-source list endpoint returns the
  type as a string. The editor doesn't try to convert; on save, it round-trips
  whatever it loaded. New steps default `dataSourceType: 1` (Database).
- `previewResults` from a step result is `List<IDictionary<string, object?>>`
  on the wire; rows arrive as JSON objects with arbitrary keys, so we union
  keys for column headers in `PreviewResultsCard`.

## Files touched

- `Beacon.Core/Handlers/Queries/UpdateQueryHandler.cs` (new)
- `Beacon.Core/Handlers/Queries/ExecuteQueryPreviewHandler.cs` (new)
- `Beacon.Core/Handlers/Queries/ExecuteStepPreviewHandler.cs` (new)
- `Beacon.SampleProject/Endpoints/QueriesEndpoints.cs` (edit)
- `Beacon.SampleProject/web/package.json` (+`@monaco-editor/react`)
- `Beacon.SampleProject/web/src/App.tsx` (route)
- `Beacon.SampleProject/web/src/components/ui/SqlEditor.tsx` (new)
- `Beacon.SampleProject/web/src/routes/queries/queries.ts` (new types + hooks)
- `Beacon.SampleProject/web/src/routes/queries/QueryDetailPage.tsx` (editHref)
- `Beacon.SampleProject/web/src/routes/queries/QueryEditorPage.tsx` (new)
- `Beacon.SampleProject/web/src/routes/queries/QueryEditorPage.test.tsx` (new)
- `Beacon.SampleProject/web/src/routes/queries/parts/QueryStepsCard.tsx` (prop rename, Link)
- `Beacon.SampleProject/web/src/routes/queries/parts/StepParameterDialog.tsx` (new)
- `Beacon.SampleProject/web/src/routes/queries/parts/PreviewResultsCard.tsx` (new)
- `Beacon.SampleProject/wwwroot/app/**` (rebuilt assets)

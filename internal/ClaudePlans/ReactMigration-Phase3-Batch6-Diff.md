# Phase 3 Batch 6 — Specialty pages (MCP + Data Quality)

Branch: `feat/react-phase3` (worktree `semantico-react`).
Top commit before: `67a5c4f` (batch 5f — query editor).

## Pages shipped

All routes mount under React Router basename `/app`. Slugs added to
`feature-flags.ts MIGRATED_PAGES` so the sidebar links light up.

| Route | File | Notes |
|---|---|---|
| `/data-catalog` | `routes/data-catalog/DataCatalogPage.tsx` | Search + filter (data source, schema, quality band). Card grid, capped at 100 results. |
| `/data-quality` | `routes/data-quality/DataQualityPage.tsx` | Overview cards per data source + contracts table. Create / edit / delete via `CreateDataContractDialog`. |
| `/data-quality/:id` | `routes/data-quality/DataContractDetailPage.tsx` | Tabs: Rules, Evaluations, Latest results. Hero metrics + evaluate-now button + delete confirm. |
| `/mcp-playground` | `routes/mcp/McpPlaygroundPage.tsx` | Chat-style tool runner. Per-tool input panels (`ask`, `search`, `query`, `get_documentation`, `get_context`). |
| `/mcp-learning` | `routes/mcp/McpLearningPage.tsx` | Stats hero + tabs: Learned patterns (approve/reject), Doc patches (apply/reject), Problem tables. |
| `/mcp-settings` | `routes/mcp/McpSettingsPage.tsx` | Admin-gated. RHF + Zod. Tabs: Pre-prompt, Tool descriptions, Guardrails (incl. learning sub-section). |

Shared modules:

- `routes/data-quality/queries.ts` — typed wrappers over
  `/beacon/api/data-quality/*` (overview, contracts list/get/create/update/delete,
  evaluate, evaluation history). Mirrors `Beacon.Core.Models.DataQuality.*`.
- `routes/data-quality/CreateDataContractDialog.tsx` — single-form modal
  (create + edit). Reuses `Dialog`. Repeating rules block; recipient
  multi-select shown only when "Alert on failure" is on.
- `routes/data-catalog/queries.ts` — `useDataCatalogQuery`.
- `routes/mcp/queries.ts` — Mcp settings, tools list/run, learning stats,
  patterns, doc patches. Includes enum constants + label maps.

## Backend additions

Three new MediatR slices:

- `Beacon.Core/Handlers/DataQuality/GetEvaluationHistoryHandler.cs` —
  `GetEvaluationHistoryQuery` → `GetEvaluationHistoryResult` (delegates to
  `IDataQualityEvaluationService.GetEvaluationHistoryAsync`).
- `Beacon.Core/Handlers/Mcp/RunMcpToolHandler.cs` — two handlers:
  - `RunMcpToolCommand` → `RunMcpToolResult` (wraps `IMcpPlaygroundService.ExecuteToolAsync`).
  - `GetMcpToolsQuery` → `GetMcpToolsResult` (returns `IMcpPlaygroundService.ToolNames`).

Endpoint wiring:

- `DataQualityEndpoints` — `GET /beacon/api/data-quality/contracts/{id}/evaluations` (`GetEvaluationHistory`).
- `McpEndpoints` — `GET /beacon/api/mcp/tools` (`GetMcpTools`),
  `POST /beacon/api/mcp/tools/run` (`RunMcpTool`).
- `McpEndpoints` — `PUT /beacon/api/mcp/settings` now requires
  `BeaconApiEndpoints.AdminPolicyName`. `GET /settings` stays open (other
  pages read it). Route-level admin gate on `/app/mcp-settings` keeps the
  UI honest.

## Test coverage

- `routes/data-quality/DataContractDetailPage.test.tsx` — renders contract
  metrics + Rules tab against a stubbed `/beacon/api/data-quality/contracts/7`
  + `/evaluations` response. All 9 vitest files / 13 tests still green.
- .NET: 35 NUnit tests still pass (incl. `OpenApiContractTests` —
  every new MediatR handler exposed via HTTP).

## Deferred / scope notes

- **CreateDataContractDialog stepper.** The Phase 3 spec called for
  `StepperDialog` here. The Blazor original is a single MudDialog form, so
  this batch ships a single-form modal too. Rule editor is a repeating
  block, not a stepper. Promote to multi-step later if UX research demands.
- **McpSettings "Context preview" tab.** The Blazor page uses reflection
  to call `Beacon.AI.IKnowledgeGraphService.GetProjectContextForLlmAsync`.
  Not yet exposed via the REST API. The React page documents this and
  links back to `/beacon/settings/mcp` for the preview. Follow-up: add a
  `GetProjectContextForLlmQuery` handler + endpoint and a Context preview tab.
- **Mermaid diagrams in McpLearning.** Task description mentioned them,
  but the Blazor page has no Mermaid — it's a pure stats + table view.
  Implemented as such.
- **DataCatalog navigation.** Card click navigates to
  `/data-sources` (matches Blazor behaviour). Detail-table drill-down can
  come later when schema-table detail pages exist in React.
- **NSwag codegen not regenerated.** The new endpoints are reachable via
  hand-typed `fetchJson` wrappers (mirroring Phase 3 Batch 4 admin-settings
  pattern). Run `npm run codegen` against a live backend to refresh
  `api/generated/beacon-api.ts` before consolidating.

## Build / sync

- `dotnet build`: green (4 pre-existing warnings, 0 errors).
- `dotnet test`: 35 / 35 pass.
- `npm run build`: green; `web/dist/` rsynced into
  `Beacon.SampleProject/wwwroot/app/`.
- `npm test`: 13 / 13 pass.
- DSWA cache cleared (`*.dswa.cache.json`, `*.Up2Date`) per project memory.

## Files changed

Added (backend):
- `Beacon.Core/Handlers/DataQuality/GetEvaluationHistoryHandler.cs`
- `Beacon.Core/Handlers/Mcp/RunMcpToolHandler.cs`

Edited (backend):
- `Beacon.SampleProject/Endpoints/DataQualityEndpoints.cs`
- `Beacon.SampleProject/Endpoints/McpEndpoints.cs`

Added (web):
- `routes/data-catalog/{queries.ts,DataCatalogPage.tsx}`
- `routes/data-quality/{queries.ts,DataQualityPage.tsx,DataContractDetailPage.tsx,CreateDataContractDialog.tsx,DataContractDetailPage.test.tsx}`
- `routes/mcp/{queries.ts,McpPlaygroundPage.tsx,McpLearningPage.tsx,McpSettingsPage.tsx}`

Edited (web):
- `App.tsx` — 6 new lazy routes.
- `feature-flags.ts` — 5 new slugs in `MIGRATED_PAGES`.
- `wwwroot/app/**` — synced from `web/dist/`.

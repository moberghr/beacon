# React Migration — Phase 1 Final Behavioral Diff

**Date:** 2026-05-07
**Branch:** `feature/react-migration-phase1`
**Spec:** `ClaudePlans/ReactMigration-Phase1.md`
**Predecessor:** `ClaudePlans/ReactMigration-Phase1-Batches1-3-Diff.md`
**PR:** #10

## Goal achieved

Every existing MediatR handler is now reachable via HTTP under `/beacon/api/*`. The React shell can call any current backend operation. Phase 3 page migration can begin without backend gaps. **No Blazor page migrated; no MediatR handler modified.**

## Final endpoint inventory

**79 operations** across **66 paths** (verified via `/openapi/v1.json`):

| Tag | Operations |
|---|---|
| Auth | 3 (`/auth/me`, `/auth/permissions`, `/csrf` — Phase 0 carry-over) |
| Health | 1 (`/health` — Phase 0 carry-over) |
| Beacon.SampleProject | 7 (existing `LoginEndpoints` / `SetupEndpoints` — untouched) |
| Projects | 10 (Batch 4) |
| QueryFolders | 4 (Batch 5) |
| Queries | 3 (Batch 5) |
| QueryVersions | 4 (Batch 5) |
| Approvals | 4 (Batch 6) — publishes `ApprovalUpdated` SignalR events |
| ApiKeys | 3 (Batch 6) |
| Dashboards | 11 (Batch 7) |
| DataQuality | 7 (Batch 8) |
| DataCatalog | 1 (Batch 8) |
| Mcp | 8 (Batch 9 — settings + learning) |
| AiActors | 13 (Batch 10) |

**68 new feature endpoints** wrapping all 81 MediatR handlers (after deduplicating Beacon.AI vs Beacon.Core overlaps for AiActor — DI picks the registered concrete handler).

## What changed by batch

| Batch | Files added/modified | Endpoints |
|---|---|---|
| 1 — JSON 401 + exception middleware | `Beacon.UI/ServiceExtensions.cs`, `Beacon.SampleProject/Middleware/ApiExceptionMiddleware.cs`, `Beacon.SampleProject/Endpoints/{BeaconApi,Auth}Endpoints.cs`, `Program.cs` | — (infra) |
| 2 — Integration test harness | `Beacon.Tests/Integration/Api/BeaconWebApplicationFactory.cs`, `Phase1HarnessTests.cs`, `Beacon.Tests.csproj` (added `Microsoft.AspNetCore.Mvc.Testing`), `Program.cs` (`public partial class Program;` shim) | — (infra) |
| 3 — SignalR hub | `Beacon.SampleProject/Hubs/BeaconHub.cs`, `SignalR/{HubUserIdProvider,HangfireSignalRJobFilter}.cs`, `web/src/lib/hub.ts`, `Program.cs`, `web/package.json` (`@microsoft/signalr`) | — (infra) |
| 4 — Projects | `Beacon.SampleProject/Endpoints/ProjectsEndpoints.cs` | 10 |
| 5 — Query domain | `Beacon.SampleProject/Endpoints/{QueryFolders,Queries,QueryVersions}Endpoints.cs` | 11 |
| 6 — Approvals + ApiKeys | `Beacon.SampleProject/Endpoints/{Approvals,ApiKeys}Endpoints.cs` | 7 + `ApprovalUpdated` publisher |
| 7 — Dashboards | `Beacon.SampleProject/Endpoints/DashboardsEndpoints.cs` | 11 |
| 8 — DataQuality + DataCatalog | `Beacon.SampleProject/Endpoints/DataQualityEndpoints.cs` | 8 |
| 9 — McpSettings + McpLearning | `Beacon.SampleProject/Endpoints/McpEndpoints.cs` | 7 |
| 10 — AiActors | `Beacon.SampleProject/Endpoints/AiActorsEndpoints.cs` | 13 |
| 11 — OpenAPI contract test | `Beacon.Tests/Integration/Api/OpenApiContractTests.cs` | — (CI tripwire) |
| 12 — Generated TS client | `Beacon.SampleProject/web/src/api/generated/beacon-api.ts` (4428 lines, committed), `web/src/api/client.ts` (wrapper), `web/nswag.config.json` (Net90→Net80), `.gitignore` | — (frontend) |
| 13 — Final gate | `.claude/rules/architecture.md` §2.1.1, this doc, `CLAUDE.md` (next) | — (docs) |

## Acceptance gate (run 2026-05-07)

| Gate | Result |
|---|---|
| `dotnet build -c Release --property WarningLevel=0` | green (0 errors, 4 pre-existing dependency warnings) |
| `dotnet test` | **35/35 pass** (28 pre-Phase 1 + 6 Phase 1 harness + 1 OpenAPI contract) |
| `npm run build` (in `web/`) | green (~183 KB gzipped) |
| OpenAPI contract test | passes — every MediatR request maps to an endpoint |
| Manual smoke (anon `/beacon/api/auth/permissions`) | 401 application/problem+json |
| Manual smoke (anon any feature endpoint) | 401 application/problem+json |
| Manual smoke (`/beacon`) | 200 Blazor (unchanged) |
| Manual smoke (`/beacon/mcp`) | 401 (unchanged) |
| Manual smoke (`/app/`) | 200 React shell (unchanged) |

## Convention summary (Phase 1 enforces)

- **Group-level auth.** `MapBeaconApi()` applies `RequireAuthorization(BeaconApiEndpoints.AuthPolicyName)` to the entire `/beacon/api/*` group. Endpoints opt out via `.AllowAnonymous()` (only `health`, `auth/me`, `csrf` do).
- **One endpoint = one MediatR handler.** No composition at the endpoint. Endpoints accept path + body, call `mediator.Send(...)`, return.
- **`WithName(...)` matches the request type stem.** `GetProjectsQuery` → `WithName("GetProjects")`. `CreateProjectCommand` → `WithName("CreateProject")`. The OpenAPI contract test enforces this.
- **`WithTags(...)` is the feature area.** Drives NSwag method grouping.
- **Path conventions:**
  - `GET /resource` → list query
  - `GET /resource/{id}` → detail query (404 when handler returns null)
  - `POST /resource` → create command (201 + Location for new resources)
  - `PUT /resource/{id}` → update command (204)
  - `DELETE /resource/{id}` → archive command (204)
  - `POST /resource/{id}/{verb}` → action commands (`approve`, `pause`, `evaluate`, etc.)
- **Body request DTOs** carry non-path fields. Path id is the first positional field on the MediatR command.
- **Exception → ProblemDetails** via `ApiExceptionMiddleware` scoped to `/beacon/api/*` only.
- **JSON 401** for any anonymous call to a `RequireAuthorization` endpoint (no HTML redirect).

## Known caveats / deferred to Phase 2 or 3

1. **Generated TS client is committed but not wired up.** `App.tsx` still uses the hand-written `fetchJson` via `useAuth.ts`. Phase 3 page migration will gradually replace it as real callsites appear. The pipeline (`npm run codegen`) is functional and was used to generate the committed file.
2. **No NSwag drift CI test.** Spec called for it; deferred because the generated file's regeneration depends on a running host, which doesn't fit a single `dotnet test` invocation cleanly. Manual `npm run codegen` + commit on every API change is the current contract. Phase 2 polish can add a docker-compose-based CI check.
3. **`GenerateProjectDocumentation` doesn't set `BeaconUserId` Hangfire parameter.** Means `JobStatusChanged` SignalR events don't fire for that job today. Fix is a one-line change to `GenerateProjectDocumentationHandler`, deferred per "no MediatR handler modifications in Phase 1." Track as a Phase 2 follow-up.
4. **`NotificationCreated` SignalR publisher.** Lives in `INotificationService`, which isn't a MediatR handler. Phase 3 wires it alongside the Notifications page migration.
5. **AiActor handler ambiguity.** Both `Beacon.Core.Handlers.Ai.AiActor` and `Beacon.AI.Handlers.Ai.AiActor` define handlers for the same request types. DI registration currently picks one (the AI service implementation per the audit). Phase 2 cleanup should delete the dead Core variants — but `/mtk fix` territory, not Phase 1.
6. **Dashboards `[AsParameters] GetDashboardsRequest`** uses minimal-API binding from query string. Confirm pagination params map correctly when Phase 3 hooks the dashboards list page.
7. **No file-upload endpoints.** No existing handler needs them; Phase 3 adds when a page does.

## Compounded learnings

- `GenericAttribute` `WithGroupName(...)` puts endpoints in a separate OpenAPI document — don't use it on individual feature groups (only for cross-cutting docs).
- `LoginFormAuthMiddleware` was the second hidden gate (after route registration order) preventing API endpoints from working — surfaced in Phase 0 and tightened in Phase 1 Batch 1.
- `WebApplicationFactory<Program>` needs `public partial class Program;` shim and a `[ProjectReference]` from the test project. No clever assembly attribute hacks needed.
- NSwag 14.1's `runtime` enum doesn't yet include `Net90`; use `Net80` and the runtime cross-version-loads fine for codegen purposes.

## Phase 2 scope (suggested, not started)

- Delete duplicate Core AiActor handlers (AI versions are canonical).
- Wire `GenerateProjectDocumentation` to set `BeaconUserId` so SignalR fires.
- NSwag drift CI test (docker-compose based).
- Replace `useAuth.ts` `fetchJson` with the generated client wrapper as a proof of the migration pattern.
- Remove `Microsoft.EntityFrameworkCore.InMemory` dead reference from `Beacon.Tests.csproj` (per §4.7).

## Phase 3 scope (next major effort, separate spec)

Begin Blazor page migration, feature area at a time, behind feature flags. Each migrated page gets its own slice in `web/src/pages/{area}/`. Authentication for new MediatR handlers needed by migrated pages (subscriptions, tasks, recipients, notifications, datasources, datamigration) will be added in those pages' batches.

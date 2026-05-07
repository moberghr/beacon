# React Migration — Phase 0 & 1 Spec

**Status:** Draft, awaiting approval before any code lands
**Author:** planning session 2026-05-07
**Scope:** Foundations + full REST API surface. Does NOT include any React page migration (Phase 3) or Blazor decommission (Phase 4).
**Out of scope for this spec:** Component-by-component page port plan (Phase 3 will get its own spec, sliced by feature area).

---

## Locked architectural decisions

| # | Decision | Choice |
|---|---|---|
| D1 | Hosting topology | **Same-origin.** React build output lands in `Beacon.SampleProject/wwwroot/`. Single Kestrel host. No CORS in production. |
| D2 | UI framework | **React 18 + TypeScript + Vite.** Shadcn/ui + Tailwind CSS + TanStack Table + TanStack Query + React Hook Form + Zod + React Router v6. |
| D3 | Real-time channel | **SignalR.** One hub for job/notification/approval push. `@microsoft/signalr` client. |
| D4 | Type contracts | **OpenAPI → TypeScript codegen via NSwag.** Build target emits `web/src/api/types.ts` and a typed fetch client from the running OpenAPI document. No hand-maintained DTOs. |

These are not revisited in later phases without an explicit decision-change ADR.

---

## Phase 0 — Foundations

**Goal:** Have an empty React app served by Kestrel at the same origin as the existing Blazor UI, with cookie auth flowing end-to-end and the codegen pipeline producing typed bindings from a real API surface (even if that surface is just `/api/auth/me`).

**Definition of done:**
1. `cd Beacon.SampleProject && dotnet run` serves both:
   - Blazor UI at existing routes (untouched)
   - React app at `/app/*` (new) — initially a single page that displays "logged in as {name}" pulled from `/api/auth/me`
2. `dotnet build` runs the React build via MSBuild target and copies output into `wwwroot/app/`
3. `npm run dev` (Vite dev server) proxies `/api/*` and `/beacon/api/*` to Kestrel for fast inner loop
4. NSwag emits `web/src/api/types.ts` from `/swagger/v1/swagger.json` and the build fails if the file is stale
5. Auth round trip works: visiting `/app` while unauthenticated redirects to existing Blazor login, then returns to the React shell with the cookie set
6. Existing Blazor UI behavior is **unchanged** — no regression in any existing flow

### Project layout

```
Beacon.SampleProject/
├── web/                              # NEW — React source, not deployed directly
│   ├── package.json
│   ├── tsconfig.json
│   ├── vite.config.ts
│   ├── tailwind.config.ts
│   ├── components.json               # shadcn config
│   └── src/
│       ├── main.tsx
│       ├── App.tsx
│       ├── api/                      # generated (gitignored) + thin wrappers
│       ├── auth/                     # useAuth, RequireAuth
│       ├── lib/                      # query client, signalr client
│       ├── components/ui/            # shadcn primitives (generated)
│       └── routes/                   # placeholder
├── wwwroot/
│   └── app/                          # build output, gitignored, served at /app
├── Endpoints/                        # NEW — minimal API endpoint groups
│   ├── ApiEndpoints.cs               # MapBeaconApi() composition root
│   ├── AuthEndpoints.cs              # /api/auth/me, /api/auth/permissions
│   └── (one file per feature area to come in Phase 1)
└── BeaconSampleProject.csproj        # extended with React build targets
```

Justification for `web/` inside `Beacon.SampleProject`: same-origin decision (D1) means the React app is part of the host's deliverable. Splitting into a separate top-level project would need Docker or another build coordinator; we have neither today (§7.1).

### Endpoints introduced in Phase 0

All under `/api/` (note: existing auth endpoints stay at `/beacon/api/auth/*` — no path break).

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/auth/me` | Returns `{ userId, displayName, email, isAuthenticated, roles[] }`. Wraps `IBeaconUserContext`. |
| GET | `/api/auth/permissions` | Returns `{ permissions[] }`. Wraps `IBeaconAuthorizationProvider`. Used by `<RequirePermission>` in React. |
| GET | `/api/health` | Liveness probe; anonymous. |

These three exist solely to validate the auth + codegen + same-origin pipeline. They're real endpoints that React will keep using.

### Build pipeline

`Beacon.SampleProject.csproj` gets a `Target` that:
1. On `BeforeBuild` (Release only by default; Debug opt-in via property): runs `npm ci` if `node_modules` is missing
2. Runs `npm run build` in `web/`
3. Vite outputs to `web/dist/`, target copies `dist/*` into `wwwroot/app/`
4. `NSwag` runs after `Build` against the produced assembly to emit `web/src/api/types.ts`

`npm run dev` workflow (developer machine, not CI):
- Vite dev server on `http://localhost:5173/app/`
- Proxies `/api`, `/beacon/api`, `/beacon/mcp`, `/hangfire` to `https://localhost:5001`
- Cookie set by Kestrel's auth flow is shared because Vite proxy preserves `Set-Cookie`

### Program.cs changes

Add, in this exact spot to preserve middleware order (§1.9):

```
... existing UseAuthentication, UseAuthorization, BeaconCookieAuthMiddleware,
    LoginFormAuthMiddleware ...

app.MapBeaconApi();          // NEW — composition root for /api endpoints
app.MapHub<BeaconHub>("/api/hub").RequireAuthorization(); // Phase 1, scaffolded in Phase 0

// Existing:
app.MapMcp("/beacon/mcp").RequireAuthorization();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

// NEW — fallback for /app/* to React index.html:
app.MapFallbackToFile("/app/{**path}", "app/index.html");
```

`MapFallbackToFile` is scoped to `/app/*` so Blazor's existing fallback for everything else is unaffected. **No** `app.UseCors()` — same-origin (D1).

### Antiforgery

Add `services.AddAntiforgery()` and require tokens on POST/PUT/PATCH/DELETE endpoints under `/api/`. The fetch client emits `X-XSRF-TOKEN` from a cookie issued by a `/api/auth/csrf` endpoint that React calls on first load. Keep Hangfire's `IgnoreAntiforgeryToken = true` as-is.

### Shared auth contract

The cookie scheme stays `CookieAuthenticationDefaults.AuthenticationScheme` (§1.8 unchanged). Login flow stays at `/beacon/api/auth/*` — React calls those exact endpoints. The OIDC challenge at `/beacon/api/auth/sso/challenge` works as-is because same-origin.

---

## Phase 1 — REST API surface for all 85 MediatR handlers

**Goal:** Every MediatR handler reachable via HTTP under `/api/`, with auth, with antiforgery on writes, with OpenAPI documenting it, with TypeScript types generated.

**Definition of done:**
1. All 85 handlers exposed (counted from the audit: 68 in `Beacon.Core` + 17 in `Beacon.AI`)
2. SignalR hub `/api/hub` live with at least three event types: `JobStatusChanged`, `NotificationCreated`, `ApprovalUpdated`
3. OpenAPI document validates and `npx @apidevtools/swagger-cli validate` passes in CI
4. NSwag-generated TypeScript covers all endpoints; React's `web/` compiles against generated types
5. Integration tests (NUnit, §4.1) cover at minimum: auth/me happy path, auth/me anon path, three sample handlers (one Core, one AI, one with file output), antiforgery rejection on missing token
6. No regression in existing Blazor UI

### Endpoint conventions

- **One endpoint = one MediatR handler.** Don't compose multiple handlers in one endpoint; React composes at the call site via React Query.
- **Endpoint groups:** one `*Endpoints.cs` static class per feature area, exposing `IEndpointRouteBuilder.Map{Area}()`. Composed in `MapBeaconApi()`.
- **Routing scheme:**
  - `GET /api/projects` ← `GetProjectsQuery`
  - `GET /api/projects/{id}` ← `GetProjectDetailsQuery`
  - `POST /api/projects` ← `CreateProjectCommand`
  - `PUT /api/projects/{id}` ← `UpdateProjectCommand`
  - `DELETE /api/projects/{id}` ← `ArchiveProjectCommand` (soft delete; §2.14)
  - Action-style for non-CRUD: `POST /api/queries/{id}/execute`, `POST /api/approvals/{id}/approve`
- **Authorization:** every endpoint `.RequireAuthorization()` by default; opt out only for `/api/health`. Use existing `IBeaconAuthorizationProvider` policies, do not invent a parallel scheme.
- **Cancellation tokens:** every endpoint takes `CancellationToken` and forwards to `IMediator.Send(..., ct)` (§5.8 already requires this in handlers).
- **Errors:** existing handlers throw `InvalidOperationException` / `BeaconException` (§9.8). Add a single exception-handling middleware that maps:
  - `InvalidOperationException` → 400 with `{ error: message }`
  - `BeaconException` → status code from `BeaconException.StatusCode` (or 400 if unset)
  - `UnauthorizedAccessException` → 403
  - Anything else → 500, log with context, return generic message (§1.11 — no PII).
- **No new MediatR features needed.** Handlers stay untouched. This is a pure HTTP wrapping layer.

### Endpoint group rollout order (within Phase 1)

Order chosen to surface tooling problems early, then maximize unblocking value for Phase 3:

1. **Auth completion** — `/api/auth/me`, `/api/auth/permissions`, `/api/auth/csrf` (Phase 0 carryover)
2. **Projects + Query Folders + Query Versions** (16 handlers) — tests path planning and parent/child resource conventions
3. **Queries + Query Execution** (2 handlers + execute action) — touches Dapper hot paths (§5.11), proves long-running endpoints work with the 5-min Kestrel timeout (§6.3)
4. **Subscriptions, Tasks, Recipients, Notifications**
5. **Data Sources, Data Quality, Data Migration**
6. **MCP Settings, MCP Learning, API Keys, Approvals**
7. **Dashboards** (11 handlers)
8. **AI Actors + AI Conversations** (13 handlers) — depends on `LlmRequestQueue` (§6.1); validates queue behavior under HTTP concurrency

### SignalR hub

```csharp
[Authorize]
public class BeaconHub : Hub
{
    // Pure broadcast hub initially. No client-to-server methods.
    // Server-side services inject IHubContext<BeaconHub> and push.
}
```

Three publishers in Phase 1:
- `Hangfire job state filter` → `JobStatusChanged` (`{ jobId, state, timestamp }`)
- `INotificationService` → `NotificationCreated` (`{ notificationId, recipientUserId, kind }`)
- Approval handler hooks → `ApprovalUpdated` (`{ approvalId, status }`)

Scoping rule: clients only receive events for their own user id (`Clients.User(...)` not `Clients.All`). Wire via `UserIdProvider` resolving from the cookie identity.

### File upload / download (deferred)

Audit confirmed no current upload paths. Phase 1 does **not** add upload endpoints. If a Phase 3 page needs upload, that page's slice introduces it. `ExportProjectDocumentationCommand` already returns `Content + ContentType + FileName` — wrap it as a streaming download endpoint without buffering into memory.

### What this phase deliberately does NOT do

- ❌ No Blazor page migration. Blazor UI continues to call `IMediator` directly. The HTTP layer is additive.
- ❌ No removal of MudBlazor, no removal of Blazor Server services, no `Beacon.UI` decommission.
- ❌ No GraphQL, no gRPC, no OData. REST + JSON only.
- ❌ No bearer JWT for the React app. Cookie auth (D1 + §1.8 + §1.9 unchanged).
- ❌ No new background services or Hangfire jobs. (§2.15 unchanged.)
- ❌ No DTO renaming. MediatR result records become JSON contracts as-is. If a record needs renaming for API clarity, that's a separate change with explicit approval.
- ❌ No changes to dual-provider migrations (§5.9). No DB schema work in this phase.

---

## Test plan

Per §4.1, NUnit + Moq + FluentAssertions + bUnit. New test surface:

1. **Endpoint smoke tests** (one per endpoint group): authenticated GET returns 200 with expected shape; unauthenticated returns 401/redirect.
2. **Antiforgery tests**: POST without token rejected; with token accepted.
3. **Translation tests** (§4.3–§4.6): no new ones required for Phase 1 since handlers are untouched. Existing translation tests must continue to pass.
4. **OpenAPI contract test**: a single test deserializes the live `/swagger/v1/swagger.json` and asserts every MediatR handler in `Beacon.Core/Handlers/**` and `Beacon.AI/Handlers/**` has a matching endpoint. Acts as a tripwire — if a new handler is added without an endpoint, this test fails.
5. **NSwag drift test**: build emits TS file; test asserts it's byte-identical to the committed file. Forces explicit regen.

---

## Risks tracked at Phase 0/1 level

| Risk | Mitigation in this spec |
|---|---|
| OpenAPI document churn breaks codegen unpredictably | Lock NSwag version; commit generated TS; drift test (§ above) |
| SignalR connection through reverse proxy / sticky session needs | Document deployment requirement: WebSockets enabled, `Connection: Upgrade` permitted. No backplane needed (single host today, §7.1) |
| Antiforgery + Vite dev proxy interaction | Vite proxy preserves cookies; CSRF cookie set by Kestrel propagates. Verify in Phase 0 acceptance test 5 |
| OIDC redirect URI must match exactly | Same-origin (D1) means the existing redirect URI works. No app registration change needed |
| `eval()` JS interop calls in current Blazor (28 interop calls audited) | Out of scope — those pages are not migrated in this phase |
| Hangfire dashboard CSRF | Untouched. Stays at current behavior. |
| Long-running handlers exceeding HTTP defaults | Kestrel keep-alive is 5 min (§6.3); confirm `HttpClient` defaults same. Endpoints inherit. No change |

---

## Acceptance gate before Phase 2 starts

1. All Phase 1 "Definition of done" items checked.
2. A dummy React route at `/app/diagnostics` lists every endpoint, hits each `GET` with no params, and shows green/red — used as a smoke harness.
3. CI pipeline updated: `dotnet build` + `dotnet test` + `npm run build` in `web/` + drift test for generated TS.
4. `.claude/rules/architecture.md` §2.x updated with the new minimal-API endpoint convention (one endpoint = one handler).
5. `CLAUDE.md` "Project Profile" section updated to mention `web/` and `/api/*`.

---

## Effort estimate (single engineer, no surprises)

- Phase 0: **5 working days**
- Phase 1: **15–20 working days**, parallelizable across feature-area slices

Cumulative through end of Phase 1: ~4–5 weeks. This is wrapping work, not invention.

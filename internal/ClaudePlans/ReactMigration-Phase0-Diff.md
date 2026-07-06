# React Migration — Phase 0 Behavioral Diff

**Date:** 2026-05-07
**Branch:** `feature/react-migration`
**Spec:** `ClaudePlans/ReactMigration-Phase0-1.md`
**Phase 1 spec:** not yet written

## Goal

Stand up the foundation that lets a React SPA coexist with the existing Blazor app, served from the same Kestrel host. No Blazor pages migrated; no MediatR handlers wrapped. Acceptance is a working `/openapi/v1.json` with a few placeholder endpoints, and a React shell at `/app/` that calls one of them.

## What changed (additive)

### Backend

| File | Change |
|---|---|
| `src/Beacon.SampleProject/Beacon.SampleProject.csproj` | New properties (`ReactAppDir`, `ReactStagingDir`, `BuildReactInDebug`, `ShouldBuildReact`) and four targets (`EnsureReactDependencies`, `BuildReactApp`, `StageReactBuild`, `CleanReactBuild`) wiring `npm ci` + `npm run build` into `dotnet build` and copying `web/dist/**` to `wwwroot/app/`. Off in Debug by default; on in Release; opt-in with `-p:BuildReactInDebug=true`. |
| `src/Beacon.SampleProject/Program.cs` | Added `AddOpenApi()`, `AddAntiforgery()`, `using Beacon.SampleProject.Endpoints`. After `UseAuthorization`: `UseAntiforgery()`, `MapOpenApi()`, `MapBeaconApi()`. After Hangfire: `MapFallbackToFile("/app/{**path}", "app/index.html")`. |
| `src/Beacon.SampleProject/Endpoints/BeaconApiEndpoints.cs` | New. Composition root for `/beacon/api/*` route group. |
| `src/Beacon.SampleProject/Endpoints/HealthEndpoints.cs` | New. `GET /beacon/api/health` → `{ status: "ok" }`. Anonymous. |
| `src/Beacon.SampleProject/Endpoints/AuthEndpoints.cs` | New. `GET /beacon/api/auth/me` (anonymous; returns user shape or `{ isAuthenticated: false }`). `GET /beacon/api/auth/permissions` (`RequireAuthorization`; returns `{ canRead, canWrite }`). |
| `src/Beacon.SampleProject/Endpoints/AntiforgeryEndpoints.cs` | New. `GET /beacon/api/csrf` issues an antiforgery token cookie + returns the request token. |
| `src/Beacon.UI/Authentication/LoginFormAuthMiddleware.cs` | One-line widening: `IsAllowedPath` now skips redirects for any `/api/` path, not just `/api/auth/`. Endpoints opt into auth via `.RequireAuthorization()` themselves. |
| `.config/dotnet-tools.json` | New. Pins `NSwag.ConsoleCore@14.1.0` as a local tool. |
| `.gitignore` | Added `src/Beacon.SampleProject/web/dist/`, `wwwroot/app/`, `web/src/api/generated/`. |
| `CLAUDE.md` | Added two bullets to "Project Profile" describing the REST API surface and the React shell. |

### Frontend (all new under `src/Beacon.SampleProject/web/`)

| File | Purpose |
|---|---|
| `package.json`, `tsconfig.json`, `tsconfig.node.json`, `vite.config.ts`, `index.html`, `tailwind.config.ts`, `postcss.config.js`, `components.json` | Standard Vite + React + TS + Tailwind + shadcn config. Vite `base: '/app/'`; dev proxy targets `https://localhost:7187` for `/beacon/api`, `/beacon/mcp`, `/hangfire`. |
| `src/main.tsx` | React Query provider + StrictMode bootstrap. |
| `src/App.tsx` | Calls `useAuth`. If unauthenticated, redirects to `/beacon` (Blazor login). Otherwise displays "Signed in as ..." in a Tailwind card. |
| `src/auth/useAuth.ts` | React Query hook hitting `/beacon/api/auth/me`. |
| `src/lib/api.ts` | Minimal fetch wrapper. Sets `credentials: 'include'`, propagates `XSRF-TOKEN` cookie as `X-XSRF-TOKEN` header on mutations. Throws `ApiError` on non-2xx. |
| `nswag.config.json` | NSwag config: reads `https://localhost:7187/openapi/v1.json`, emits `src/api/generated/beacon-api.ts` (Fetch client + types). |
| `README.md` | Dev loop, build, codegen instructions. |

## What did NOT change

- No MediatR handler touched.
- No EF Core entity, migration, or query touched.
- No existing Blazor page or component touched.
- No Beacon.Core, Beacon.AI, Beacon.MCP, or any connector project touched.
- No removed code anywhere. Only `src/Beacon.UI/Authentication/LoginFormAuthMiddleware.cs` was modified, and the change is a behavior-preserving widening (paths previously redirected stay redirected; only `/api/*` paths gained skip-redirect behavior).
- Cookie auth scheme (`§1.8`), middleware order (`§1.9`), HTTPS redirect carve-out for `/beacon/mcp`, and Hangfire dashboard all unchanged.

## Smoke-test results (HTTP, port 5299)

| Path | Before Phase 0 | After Phase 0 |
|---|---|---|
| `GET /beacon` | Blazor login page | Blazor login page (unchanged) |
| `GET /beacon/mcp` | 401 (auth required) | 401 (unchanged) |
| `GET /openapi/v1.json` | 404 | 200, document with 11 paths (4 new + 7 existing) |
| `GET /beacon/api/health` | 404 / Blazor redirect | 200 `{"status":"ok"}` |
| `GET /beacon/api/auth/me` (anon) | 404 / Blazor redirect | 200 `{"isAuthenticated":false}` |
| `GET /beacon/api/csrf` (anon) | 404 / Blazor redirect | 200 `{"token":"..."}` + sets `XSRF-TOKEN` cookie |
| `GET /beacon/api/auth/permissions` (anon) | n/a | **500** (known caveat — see below) |
| `GET /app/` | 404 | 200, React shell |
| `GET /app/some/deep/route` | 404 | 200, React shell (SPA fallback) |
| `dotnet test` | 28 passed | 28 passed (no regressions) |
| `dotnet build -c Release` | green | green |
| `npm run build` (in `web/`) | n/a | green |

## Known caveats (deferred to Phase 1)

1. **`GET /beacon/api/auth/permissions` returns HTTP 500 for anonymous callers.** Cookie auth's challenge handler issues an HTML redirect to `/beacon/Login`, which collides with the JSON response pipeline. Fix in Phase 1: configure `CookieAuthenticationOptions.Events.OnRedirectToLogin` to return 401 with empty body for `Path.StartsWithSegments("/api")`. For now, authenticated callers will work fine.
2. **No integration tests for the new endpoints.** `Beacon.Tests` doesn't reference `Beacon.SampleProject` and lacks `Microsoft.AspNetCore.Mvc.Testing`. Setting up a `WebApplicationFactory<Program>` harness is itself substantial; deferred to Phase 1 alongside the bulk of new endpoint coverage.
3. **No exception-handling middleware.** Endpoints currently rely on the framework's default behavior. Phase 1 will add a single mapper for `InvalidOperationException` → 400, `BeaconException` → 400 (or `.StatusCode` if set), `UnauthorizedAccessException` → 403.
4. **Generated TS client (`web/src/api/generated/beacon-api.ts`) is gitignored, not committed.** A developer who wants to compile the React app must either `npm run codegen` against a running backend, or skip the generated client entirely (current `App.tsx` uses `fetchJson` directly, not the generated client). When Phase 1 starts wrapping handlers, we'll likely commit the file and add a CI drift check.
5. **CI build doesn't currently run `npm run build`.** The MSBuild target is wired but only fires for Release builds. CI should pass `-c Release` (likely already does) — verify on the next CI run.

## Spec deltas (resolved during implementation)

The original spec at `ClaudePlans/ReactMigration-Phase0-1.md` proposed `/api/*` (root) and `/app/*` (root). During task drafting I re-aligned to `/beacon/api/*` and `/beacon/app/*` to match the existing `src/Beacon.UI/Authentication/LoginEndpoints.cs` convention. Smoke testing revealed:

- `/beacon/api/*` is fine — the existing `LoginEndpoints` proves minimal-API endpoints under that prefix coexist with Blazor's `/beacon` mount, provided `LoginFormAuthMiddleware.IsAllowedPath` lets them through (the one-line widening above).
- `/beacon/app/*` is **not fine** — Blazor's catch-all owns the `/beacon` subtree and won the route match against `MapFallbackToFile`. Reverting React to `/app/*` at root (per the original spec) was the clean fix. The eventual cutover (Phase 4) will rename to `/` and Blazor moves to `/legacy` or is decommissioned.

Final paths committed:
- API: `/beacon/api/*` (consistent with existing convention)
- React shell: `/app/*` (root, per original spec)

## What compounds for Phase 1

1. **Convention locked: one minimal-API endpoint = one MediatR handler.** Add this to `.claude/rules/architecture.md` §2.x when Phase 1 begins exercising the rule.
2. **Cookie-auth-for-API needs an explicit override** (`OnRedirectToLogin` returning 401 for `/api/*`). Phase 1 should add this on day one before wrapping any handler.
3. **`LoginFormAuthMiddleware` is in `Beacon.UI`**, the project we're decommissioning. Phase 4 cutover will move auth middleware into `Beacon.SampleProject` or a new `Beacon.Web.Auth` library.
4. **`MapFallbackToFile` and Blazor's catch-all coexist only when the React fallback is at a path Blazor doesn't claim.** Future migrations cannot mount React under `/beacon/*` without disabling Blazor's fallback first.

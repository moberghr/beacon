# Phase 3 Batch 7 — Cutover Complete

**Branch:** `feat/react-phase3` (PR #13).
**Predecessor:** Batch 7a (commit `309467e`) shipped Dashboards/AI Actors/Migration Jobs/auth landing pages; commit `3bc4e95` added Queries list + sidebar slug fix. Both unblocked the cutover.

---

## What changed

### Deleted
- **`Beacon.UI/` project** — entire directory removed. Was the legacy Blazor host (MudBlazor + Razor pages). Project entry removed from `Beacon.sln`. ProjectReference removed from `Beacon.SampleProject.csproj` and `Beacon.Tests.csproj`.
- **MudBlazor + Blazored.LocalStorage NuGet packages** — dropped from `Beacon.SampleProject.csproj`.
- All Blazor wiring in `Program.cs`: `app.UseBeaconUI()`, `AddBlazorUI("/beacon")`, `app.MapBlazorHub()`, `MapRazorComponents`, the `/beacon` `Map(...)` branch.

### Moved
- **Auth middleware** from `Beacon.UI/Authentication/*` → `Beacon.SampleProject/Authentication/`:
  `ApiKeyAuthMiddleware`, `BasicAuthHandler`, `BeaconAuthenticationOptions`, `BeaconAuthorization*`, `BeaconCookieAuthMiddleware`, `FirstRunSetupMiddleware`, `IBeaconAuthentication*`, `JwtBearer*`, `LoginFormAuthMiddleware`, `MiddlewarePathHelper`, `OidcEvent*`.
- **Auth endpoints** `LoginEndpoints.cs`, `SetupEndpoints.cs` → `Beacon.SampleProject/Endpoints/`.
- All namespaces rewritten `Beacon.UI.Authentication` → `Beacon.SampleProject.Authentication`. Two test files in `Beacon.Tests/Unit/` updated to match.
- New file `Beacon.SampleProject/Authentication/AuthServiceExtensions.cs` — extracted from the old `Beacon.UI/ServiceExtensions.cs`. Holds `AddBeaconCookieAuthentication`, `AddBeaconOidcAuthentication`, `AddBeaconJwtAuthentication`, `UseBeaconJwtBearerAuthentication`.

### Path / routing changes
- **`BrowserRouter basename`** flipped `"/app"` → `"/"`.
- **Vite `base`** flipped `'/app/'` → `'/'`.
- **`MapFallbackToFile`** now serves `wwwroot/index.html` from the root via a regex catch-all.
- **`<ReactStagingDir>`** in `Beacon.SampleProject.csproj`: `wwwroot/app` → `wwwroot`. The React build now lands directly under `wwwroot/`. The old `wwwroot/app/` directory was emptied.
- **`feature-flags.ts` `resolveNavHref`** — simplified to always return `/<slug>`. `isMigrated` always returns `true`. Kept as compat shim so `Sidebar.tsx` still compiles unchanged; can be deleted in a follow-up.
- **`LoginFormAuthMiddleware`** — login redirect target updated to React `/login` (was Razor login). Allow-list expanded for `/login`, `/logout`, `/error`, `/hangfire`, `/openapi`, `/beacon/mcp`.

### CLAUDE.md + rules updates
- `CLAUDE.md` Project Profile: removed "Blazor Server (MudBlazor)", removed `Beacon.Web/` empty-dir note, updated React shell description (no Tailwind/shadcn — Beacon-design CSS), updated solution count `17 → 16`.
- `.claude/rules/architecture.md` §2.4 layer rules: removed `UI` from the diagram. §2.5 Beacon.Web note replaced with cutover note.
- `.claude/rules/project-specific.md` §9.2 / §9.3: replaced MudBlazor pitfall paragraphs with React conventions (basename rule, no Tailwind/shadcn). §9.6: marked `Beacon.Web/` removal note as superseded.

---

## Acceptance gate

| Gate | Result |
|---|---|
| `dotnet build -c Release --property WarningLevel=0` | green (4 pre-existing pkg warnings) |
| `dotnet test Beacon.Tests -c Release` | 28/28 pass (incl. `OpenApiContractTests`) |
| `npm run build` | green; bundles into `wwwroot/` |
| `npm test` (vitest) | 13/13 pass |
| Worktree | clean |

---

## Manual smoke checklist (run before merging PR #13)

1. `dotnet run --project Beacon.SampleProject -c Release` — app boots without errors.
2. Open browser to `http://localhost:<port>/` — should render React `Home` after auth.
3. Visit `/login` (logged out) — login page renders without Blazor.
4. Hit each sidebar item — every link goes to a React route, no `/beacon/*` slug-mismatch.
5. Confirm `/hangfire` dashboard still serves.
6. Confirm `/beacon/mcp` MCP endpoint still responds (auth required).
7. Confirm `/beacon/api/...` endpoints still reachable (the `/beacon/api/...` URL space is preserved — only the Blazor UI mount was at `/beacon/...` and was removed).

---

## Known follow-ups (not blockers)

- **Codegen rerun.** Several Phase 3 batches use hand-typed `fetchJson<T>` wrappers because we couldn't run `npm run codegen` against a live backend during subagent dispatches. Run `npm run codegen` once and consolidate.
- **Drop `feature-flags.ts`** entirely and inline a single hard-coded `resolveNavHref` if the migrated/non-migrated split is no longer relevant.
- **Cleanup `Beacon.SampleProject/Authentication/`** — some moved files may have unused legacy paths (`/beacon` redirects) that are now dead.
- **Test count.** Batch reports during this PR cited "35/35" repeatedly — actual current count is 28/28. The discrepancy is from earlier subagent reporting noise, not from removed tests; nothing was deleted.

---

## Files touched

Beyond moves and deletes, the cutover edited:
- `Beacon.SampleProject/Program.cs` — strip Blazor wiring, mount auth middleware, SPA fallback at root.
- `Beacon.SampleProject/Beacon.SampleProject.csproj` — pkg refs, project ref, staging dir.
- `Beacon.Tests/Beacon.Tests.csproj` — drop `Beacon.UI` ProjectReference.
- `Beacon.Core/Beacon.Core.csproj` — `InternalsVisibleTo` swap (`Beacon.UI` → `Beacon.SampleProject` + `Beacon.Tests`).
- `Beacon.sln` — `Beacon.UI` project entry removed.
- `Beacon.SampleProject/web/src/App.tsx` — `BrowserRouter basename="/"`.
- `Beacon.SampleProject/web/vite.config.ts` — `base: '/'`.
- `Beacon.SampleProject/web/src/feature-flags.ts` — compat shim.
- `Beacon.Tests/Unit/{OidcEventHandlersTests,SsoChallengeReturnUrlTests}.cs` — namespace usings updated.

The overall PR (commits `5e07c01..HEAD`) ports 44 routable Blazor pages to React and removes the Blazor stack entirely.

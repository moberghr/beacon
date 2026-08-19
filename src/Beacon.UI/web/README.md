# Beacon React shell

React 18 + TypeScript + Vite + Tailwind CSS v3.4. This is the whole Beacon UI — it replaced the
Blazor/MudBlazor shell in the Phase 3 cutover.

The app lives here (`src/Beacon.UI/web`), builds into `src/Beacon.UI/wwwroot`, and ships inside the
`Beacon.UI` Razor Class Library, which serves it at the **root URL `/`**. `BrowserRouter` is mounted at
`basename="/"` — in-app links are `/projects`, never `/app/projects`.

## Dev loop

```bash
# Terminal 1: backend (from the repo root)
dotnet run --project Beacon.SampleProject --no-launch-profile

# Terminal 2: Vite
npm install --prefix src/Beacon.UI/web
npm run dev --prefix src/Beacon.UI/web
# → http://localhost:5173
```

Vite proxies `/beacon/api/*` (including the SignalR hub) and `/beacon/mcp` to `https://localhost:7187`
— override with the `BEACON_BACKEND_URL` env var. Cookies pass through, so logging in through the proxied
app authenticates the shell.

### No backend? Use mock mode

```bash
npm run dev:mock --prefix src/Beacon.UI/web
```

Runs the full UI against in-browser MSW fixtures (`VITE_MOCK_API=1`). This is the mode the documentation
screenshots are taken in.

## Production build

`dotnet build src/Beacon.UI -c Release` (or building anything that references it) runs `npm ci && npm run build`
and stages the output into `src/Beacon.UI/wwwroot`, which is published as static web assets.

To force the React build in Debug: `dotnet build -c Debug -p:BuildReactInDebug=true`.

## TypeScript codegen

`/openapi/v1.json` is the contract. NSwag generates a typed fetch client.

```bash
# 1. Start the backend (must be reachable at https://localhost:7187)
dotnet run --project Beacon.SampleProject

# 2. Regenerate
npm run codegen --prefix src/Beacon.UI/web
```

Output lands at `src/api/generated/beacon-api.ts` — do not hand-edit.

## Tests

```bash
npm run test --prefix src/Beacon.UI/web        # vitest run
npm run test:watch --prefix src/Beacon.UI/web
```

Vitest + React Testing Library + MSW, colocated with the code under test.

## Where things live

- `src/components/beacon/` — the Beacon design system primitives (`Button`, `Card`, `Pill`, `KPI`, `Banner`,
  `Modal`, `Input`/`Field`, `Seg`, `Kbd`, `PageHeader`, `BeaconHero`). Use these instead of hand-rolling chrome.
- `src/index.css` — CSS variables under `:root` that the Tailwind config maps onto semantic names
  (`bg-brand-500`, `bg-surface`, `text-text-muted`, `bg-ok-bg`, …), plus the `@layer components` helpers.
  Dark theme via `document.documentElement.dataset.theme = 'dark'`.
- `src/routes/<area>/` — one folder per page area, with its `queries.ts` alongside the components.
- `src/lib/api.ts` — fetch wrapper handling cookies + CSRF header.
- `src/auth/useAuth.ts` — React Query hook for `/beacon/api/auth/me`.
- `src/api/generated/` — NSwag output, do not hand-edit.
- `src/mocks/` — MSW handlers backing `dev:mock` and the tests.

Icons come from `lucide-react`.

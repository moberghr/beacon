# Beacon React shell

React + TypeScript + Vite + Tailwind. Replaces Blazor incrementally; Phase 0 only renders an auth-aware placeholder.

## Dev loop

```bash
# Terminal 1: backend
dotnet run --project ../   # from Beacon.SampleProject

# Terminal 2: Vite
cd Beacon.SampleProject/web
npm install
npm run dev
# → http://localhost:5173/app/
```

Vite proxies `/beacon/api/*`, `/beacon/mcp`, and `/hangfire` to `https://localhost:7187` (override with `BEACON_BACKEND_URL` env var). Cookies pass through, so logging in via `https://localhost:7187/beacon` makes the React shell authenticated.

## Production build

`dotnet build Beacon.SampleProject -c Release` runs `npm ci && npm run build` and stages the output into `Beacon.SampleProject/wwwroot/app/`. Kestrel serves it at `/app/*`.

To force the React build in Debug: `dotnet build -c Debug -p:BuildReactInDebug=true`.

## TypeScript codegen

`/openapi/v1.json` is the contract. NSwag generates a typed fetch client.

```bash
# 1. Start the backend (must be reachable at https://localhost:7187)
dotnet run --project Beacon.SampleProject

# 2. Regenerate
cd Beacon.SampleProject/web
npm run codegen
```

Output lands at `src/api/generated/beacon-api.ts` (gitignored — regenerate as needed). For Phase 1, this becomes a CI drift check.

## Where things live

- `src/lib/api.ts` — minimal fetch wrapper handling cookies + CSRF header
- `src/auth/useAuth.ts` — React Query hook for `/beacon/api/auth/me`
- `src/components/ui/` — shadcn primitives go here (none yet)
- `src/api/generated/` — NSwag output, do not hand-edit

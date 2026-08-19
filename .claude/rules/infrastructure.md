# Infrastructure

> §7.x — Hosting, CI, deployment. Loaded automatically by Claude Code.

§7.1 **Hosting:** ASP.NET Core self-hosted (Kestrel) serving the React SPA at root `/` via the `Beacon.UI` Razor Class Library (Blazor/MudBlazor were removed in the Phase 3 cutover). No containerization is configured today — there is no `Dockerfile` in the repo.

§7.2 **CI/CD:** GitHub Actions, defined in `.github/workflows/w-build.yml`. Triggers on release or manual dispatch. Pipeline: restore → build → test → pack → push to NuGet.

§7.3 **NuGet publishing:** Projects with `<IsPackable>true</IsPackable>` are pushed to NuGet on release. Do not flip `IsPackable` without coordinating with the release process.

§7.4 **Background jobs:** Moberg.Warp backed by PostgreSQL (dedicated `WarpDbContext`, `warp` schema). Dashboard mounts at `/warp` (admin-only, via `WarpDashboardAuthFilter : IWarpAuthorizationFilter`). The `/warp` path is allow-listed in `LoginFormAuthMiddleware`. Warp replaced Hangfire.

§7.5 **MCP server:** Streamable HTTP transport, mounted at `/beacon/mcp` via `ModelContextProtocol.AspNetCore`. Wired with `app.MapMcp("/beacon/mcp").RequireAuthorization(BeaconApiEndpoints.ExecuteScopePolicyName)` — the Execute scope is load-bearing (§1.4: `ask`/`query` execute SQL, so a Read-scoped API key must not reach MCP). Do not move the route, weaken the policy back to bare `RequireAuthorization()`, or remove the auth requirement.

§7.7 **MCP discovery documents** are mapped by `app.MapMcpDiscovery()` and are anonymous BY DESIGN: `/.well-known/oauth-protected-resource`, its `/beacon/mcp` path-inserted variant, and `/.well-known/mcp/server-card.json`. `McpDiscoveryEndpoints.AnonymousDiscoveryPaths` is the single source of truth — the routes, `LoginFormAuthMiddleware`, and `FirstRunSetupMiddleware` all derive their allow-list from it. NEVER allow-list `/.well-known` as a prefix; match those exact paths only. Set `Beacon:PublicBaseUrl` in any proxied deployment, or the documents reflect the client-controlled Host header (the host logs a startup warning when it is unset).

§7.6 **Composition root** is `Beacon.SampleProject` (yes, the name is historical). All cross-project DI registration, configuration loading, and Warp setup (`AddDbContext<WarpDbContext>` + `AddWarpServer` + `UseWarpUI`) live in `src/Beacon.SampleProject/Program.cs`.

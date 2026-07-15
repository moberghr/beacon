# Infrastructure

> §7.x — Hosting, CI, deployment. Loaded automatically by Claude Code.

§7.1 **Hosting:** ASP.NET Core self-hosted (Kestrel) with Blazor Server. No containerization is configured today — there is no `Dockerfile` in the repo.

§7.2 **CI/CD:** GitHub Actions, defined in `.github/workflows/w-build.yml`. Triggers on release or manual dispatch. Pipeline: restore → build → test → pack → push to NuGet.

§7.3 **NuGet publishing:** Projects with `<IsPackable>true</IsPackable>` are pushed to NuGet on release. Do not flip `IsPackable` without coordinating with the release process.

§7.4 **Background jobs:** Moberg.Warp backed by PostgreSQL (dedicated `WarpDbContext`, `warp` schema). Dashboard mounts at `/warp` (admin-only, via `WarpDashboardAuthFilter : IWarpAuthorizationFilter`). The `/warp` path is allow-listed in `LoginFormAuthMiddleware`. Warp replaced Hangfire.

§7.5 **MCP server:** Streamable HTTP transport, mounted at `/beacon/mcp` via `ModelContextProtocol.AspNetCore`. Wired with `app.MapMcp("/beacon/mcp").RequireAuthorization()`. Do not move the route or remove the auth requirement.

§7.6 **Composition root** is `Beacon.SampleProject` (yes, the name is historical). All cross-project DI registration, configuration loading, and Warp setup (`AddDbContext<WarpDbContext>` + `AddWarpServer` + `UseWarpUI`) live in `src/Beacon.SampleProject/Program.cs`.

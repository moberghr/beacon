# Todo — PR #11 Pre-Merge Fixes (2026-06-01)

Spec: `docs/specs/2026-06-01-pr11-merge-fixes.md`
Plan: `docs/plans/2026-06-01-pr11-merge-fixes.md`

## Batches

- [ ] **B1** Secret hygiene — strip `.mcp.json` token, gitignore `.mcp.local.json`
- [ ] **B2** Auth policy + middleware order + API-key 401 body
- [ ] **B3** Antiforgery on logout + Hangfire dashboard auth filter
- [ ] **B4** Extract inline DI registrations into `AuthServiceExtensions`
- [ ] **B5** MCP endpoints resolve actor from claims (×3)
- [ ] **B6** Drop `OwnerUserId` from `UpdateDataContract` body + command
- [ ] **B7** Remove `OperationResult`; throw or return domain record
- [ ] **B8** Drop `blazor` from `Beacon.Core.csproj` `PackageTags`

## Post-implementation review

- [ ] `dotnet build --property WarningLevel=0` clean
- [ ] `dotnet test` 35/35
- [ ] `npm test` 16/16
- [ ] NSwag regen if any endpoint contract shifted (B6, possibly B7)
- [ ] Manual smoke: /hangfire admin gate, API-key /beacon/api/*, logout-with-stale-CSRF, MCP audit actor non-null
- [ ] Pre-commit-review checklist green
- [ ] Commit + push

## Deferred (follow-up issue)

- [ ] Translation tests for `GetControlTowerStatisticsHandler`, `GetMigrationExecutionsHandler`, `GetEvaluationHistoryHandler` (§4.6)

## React Web Quality Refactor (2026-06-02)
Spec: docs/specs/2026-06-02-react-web-quality-refactor.md · Plan: docs/plans/2026-06-02-react-web-quality-refactor.md

- [x] Batch A — Codegen regeneration + control-tower migration (delete hand-rolled api.ts)
- [x] Batch B — Typed-client refactor: remove 92 casts + duplicate interfaces across ~28 files
- [x] Batch C — Accessibility: Button aria-label, Field htmlFor, DataTable roles, Dialog focus trap
- [x] Batch D — Error-feedback: route mutation catches through describeError+toast; detail error states
- [x] Batch E — Stable list keys: HomePage feed, MCP playground
- [x] Batch F — Dashboards route guard: redirect /dashboards* to /home
- [x] Post: full build + test, behavioral diff, review

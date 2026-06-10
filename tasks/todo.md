# Todo — MCP SQL-Generation Accuracy (2026-06-09)

**Scope:** new-feature (gaps A–E) · **Branch:** create `feature/mcp-sql-accuracy`
Spec: `docs/specs/2026-06-09-mcp-sql-generation-accuracy.md`
Plan: `docs/plans/2026-06-09-mcp-sql-generation-accuracy.md`

## Batches

- [x] **B1 — Storage:** `SampleValues` on ColumnMetadata; signal fields (DryRunFailed/DryRunError/EmptyResultRetryAttempted); `EnableSampleValueCollection` setting; ColumnMetadataDto; dual migrations (PG + SQL Server, append-only). ✓ build
- [x] **B2 — Sampling:** ColumnValueSampler (engine SQL builders, PII-null, failure-tolerant) + DatabaseMetadataService wiring + DI + ColumnValueSamplerTests. ✓ build + tests
- [x] **B3 — M-Schema:** SchemaContextFormatter extraction + samples/MaxLength + FK section + SqlGenerationService prompt + SchemaContextFormatterTests. ✓ build + tests (parallel-safe with B2)
- [x] **B4 — Dry-run:** per-engine ValidateQueryAsync + ProjectAskTool/CrossSourceQueryService wiring + signal setter + RepairFlowTests (SC3). ✓ build + tests
- [x] **B5 — Empty-result retry:** single bounded retry on 0 rows + signal + tests (SC4). ✓ build + tests
- [x] **B6 — AST read-only:** SqlReadOnlyAstValidator + wiring at 3 call sites + DI + tests (SC5). ✓ full build + full test suite (SC6)

## Post-implementation review

- [x] Spec-drift check vs docs/specs/2026-06-09-mcp-sql-generation-accuracy.json
- [x] compliance-reviewer (Stage 1 — regulated: read-only + PII)
- [x] test-reviewer + architecture-reviewer (Stage 2, parallel)
- [x] LSP diagnostics clean on all changed files (§8.5, via harness diagnostics)
- [x] dotnet format --verbosity quiet
- [x] Lessons captured in tasks/lessons.md (Phase 7)

---

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

# Todo — Codebase Audit Remediation (2026-07-01)

**Scope:** bug-fix-batch · security_impact: high · Rigor: MAX (score 28)
Spec: `docs/specs/2026-07-01-audit-remediation.md`
Plan: `docs/plans/2026-07-01-audit-remediation.md`

## Batches

- [x] **B1 — Read-only core fix:** relocate `SqlReadOnlyAstValidator` MCP→Core; gate `AddQueryStep`/`UpdateQueryStep`/`ExecuteStep` ✓ build + 212/212 tests
- [x] **B2 — Connector read-only fixes:** `ApiProvider` reject mutating verbs; Databricks/BigQuery real statement check; `DatabaseProvider` statement-type check for 5 SQL connectors ✓ build + 212/212 tests
- [x] **B3 — Auth security:** remove hardcoded SMTP credential; remove admin-username escalation (`SampleClaimsTransformation` + `SampleAuthorizationProvider`) ✓ build + 212/212 tests — ⚠️ credential still needs manual rotation
- [x] **B4 — MCP/AI security:** fail-closed project scope; stop logging full SQL; route `TestLlmConnectionHandler` through `LlmRequestQueue`; real `ValidateQuerySyntaxAsync`; guardrail on cross-source joined SQL ✓ build + 212/212 tests
- [x] **B5 — Backend cleanup:** remove `SecretReEncryptionService`; `SqlIdentifierGuard` in `ColumnValueSampler`; lambda naming; dedupe table-name regex + prompt fragment + tool comment; clean 401 on malformed scopes JSON; `JsonSerializer` in `WriteJsonStatusAsync`; `IBeaconScheduler` verified already correct ✓ build + 217/217 tests
- [x] **B6 — Endpoint thinning:** `SetupEndpoints`, `McpEndpoints`, `ApprovalsEndpoints` → thin `mediator.Send()` only ✓ build + 217/217 tests
- [x] **B7 — Tests & hygiene:** 3 missing GroupBy translation tests; tests for B1/B2 read-only rejection; remove `bunit` reference; update stale `testing.md` ✓ build + 231/231 tests
- [x] **B8 — Frontend small fixes:** typed status comparisons; `useRequireAdmin()` hook; `Modal`-based drawer; `<Link>` not `<a>`; no inline styles/raw oklch ✓ build + 79/79 tests
- [x] **B9 — Frontend auth CSS migration:** delete `login.css`; rebuild 4 auth pages on Tailwind + Beacon primitives ✓ build + 79/79 tests, zero login__ refs

## Post-implementation review

- [x] Spec-drift check vs `docs/specs/2026-07-01-audit-remediation.json` — PASS, all 68 touched files accounted for
- [x] compliance-reviewer (Stage 1) — 2 mid-implementation checkpoints (after B4, after B7) + final full-diff pass; found and fixed 1 Critical (CTE-bypass) + 1 Warning (audit-actor claim regression)
- [x] test-reviewer + architecture-reviewer + silent-failure-hunter (Stage 2, MAX tier, parallel) — architecture: clean/approved; test-reviewer: approve with comments (2 warnings fixed: SampleClaimsTransformation + McpProjectContext test coverage); silent-failure-hunter: found and fixed 1 Critical (EXPLAIN-wrapped DML bypass) + 1 Warning (SQLite dialect fallthrough)
- [x] `dotnet build --property WarningLevel=0` clean — 0 errors, 0 warnings
- [x] `dotnet test` full suite green — 247/247 (up from 217 baseline)
- [x] `npm run build` + Vitest green — 79/79 tests
- [x] Lessons captured in `tasks/lessons.md` (Phase 7) — AST-validator adversarial-review lesson added
- [ ] Manual smoke test (recommended before merge, not run in this session): write-query rejection via query builder UI + MCP tool call, auth pages light/dark visual check
- [ ] **Manual follow-up required (not code, security-critical):** rotate leaked SMTP credential (`dev@netgiro.is`) in the mail provider account — treat as already compromised
- [ ] Optional follow-up (non-blocking, flagged by reviewers): add tests for `SqlTableNameExtractor`, `BeaconMailSender` config guards, `ActorUserResolver`/`CreateSuperAdminHandler`/`SignalRApprovalNotifier` branch logic; centralize SQLite dialect-string mapping across `DatabaseProvider.cs`/`QueryService.Steps.cs`; minor Modal `aria-labelledby` tightening on `SubscriptionDetailPanel`

---

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

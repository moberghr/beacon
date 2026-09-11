# Todo — Per-project MCP settings, locks, ceilings (2026-09-09)

**Scope:** new-feature · security_impact: requires-audit-trail · **Rigor: MAX** (score ≈ 22 — 5 batches, ~35 non-mechanical files → +12, 6 external contracts → +4 cap, +1 internal-tooling, security +3)
Spec: `docs/specs/2026-09-09-mcp-project-settings.md`
Plan: `docs/plans/2026-09-09-mcp-project-settings.md`
Parent: `docs/plans/2026-09-08-warehouse-engine.md` Wave 0.2
Branch: `feature/mcp-project-settings` (stacked on `feature/sql-execution-gate` @ 3f20291)

## B1 — Data model + dual migration (W0)
- [x] `McpProjectSettings` entity + DbSet + fluent config; `McpSettings` +6; `DataSource` +2; `McpQuerySignal.CallerHash`
- [x] `McpSettingsData` +6; `McpProjectSettingsData` (nullable)
- [x] PG migration scaffolded; SQL Server migration + Designer + snapshot hand-written; parity checked
- [x] `McpProjectSettingsTranslationTests` (SC6)
- [x] Checkpoint: build + translation test

## B2 — Options, provider, global handler (W1)
- [x] `McpDeploymentOptions` + validator + `ValidateOnStart`
- [x] `McpSettingsProvider`: Resolve (lock > project > global > default, clamp), change-token cache, 3 new interface members
- [x] `SettingLockedException`; `UpdateMcpSettingsHandler` +6 fields + lock check
- [x] `SettingsProviderMock` helper; `McpEffectiveSettingsTests` (SC1–SC3)
- [x] Checkpoint: build + fixture

## B3 — Project handlers, endpoints, 409 (W2)
- [x] `GetMcpProjectSettingsHandler`, `UpdateMcpProjectSettingsHandler`
- [x] `McpEndpoints` GET/PUT `/mcp/projects/{projectId}/settings`
- [x] `ApiExceptionMiddleware` 409 arm before `BeaconException`
- [x] `McpProjectSettingsHandlerTests` (SC4, SC5); `ApiExceptionMappingTests` (SC4)
- [x] Checkpoint: build + fixtures; OpenApiContractTests lists no new missing handler (SC7)

## B4 — Consumers (W3)
- [x] Tools: `ProjectQueryTool`, `DryRunTool`, `ProjectAskTool` → effective
- [x] Services: `QueryExecutionService` (+IProjectContext), `McpSignalService`, KG ×4, aggregation loop + cleanup, eval
- [x] Ten fixtures → `SettingsProviderMock` (helpers only)
- [x] Checkpoint: build + full suite vs 829/5/834; SC8 grep

## B5 — UI (W3)
- [x] `beacon-api.ts` hand-added operations; `queries.ts` hooks
- [x] `McpSettingsPage` scope selector / override / locked / six fields
- [x] `McpSettingsPage.test.tsx`
- [x] Checkpoint: `npm run build` + `npm run test -- --run`

## Final
- [x] Full `dotnet test` vs baseline; web tests; SC8/SC9
- [x] Behavioural diff; spec-drift check clean

## Post-implementation review
- [ ] Stage 1 `compliance-reviewer`; Stage 2 `test-reviewer`, `architecture-reviewer`, `silent-failure-hunter` (MAX)
- [ ] PG vs SQL Server migration parity; locks-last; cache invalidation; 409 arm order
- [ ] Update parent plan Wave 0.2 status

---

# Todo — Shared SQL execution gate (2026-09-08)

**Scope:** internal-refactoring · security_impact: requires-audit-trail · **Rigor: MAX** (score 16 — 4 batches, 20 non-mechanical files → +7, 6 external contracts → +4 (cap), security +3; hard floor HIGH via batches ≥ 3 and security)
Spec: `docs/specs/2026-09-08-sql-execution-gate.md`
Plan: `docs/plans/2026-09-08-sql-execution-gate.md`
Parent: `docs/plans/2026-09-08-warehouse-engine.md` Wave 0.1
Branch: `feature/sql-execution-gate` (off `origin/main` @ 60d686f)

## B1 — Core primitives (W0)
- [x] `SqlDialects.Resolve` shared resolver (+ azuresynapse) in `SqlReadOnlyAstValidator.cs`
- [x] `SqlReadOnlyAstValidator`: whitespace-only SQL rejected; test flipped
- [x] `SqlSchemaValidator`: `TablesUsed`, `Checked`, projection-alias awareness; tests SC5 / SC9 / Checked
- [x] `SqlRowLimitRewriter` (AST-decided, text-applied) + `SqlRowLimitRewriterTests` (SC3, 11 cases)
- [x] `QueryGuardrailService.ApplyRowLimit` delegates; 3 regression tests added; 8 existing unchanged
- [x] Checkpoint: build + 4 fixtures green (97/97)

## B2 — Gate (W1)
- [x] `ISqlExecutionGate` + records; `SqlExecutionGate` composition per verdict table
- [x] DI: `TryAddTransient<ISqlExecutionGate, SqlExecutionGate>` in Core
- [x] `Tests/Common/TestSqlGate.cs`
- [x] `SqlExecutionGateTests` (SC2 six adversarial cases, SC5, Skipped codes, BlockOnSchemaFailure, EnforceReadOnly=false semantics)
- [ ] Checkpoint: build + fixture green

## B3 — AI callers (W2)
- [x] `AskSqlPipeline` ctor + all validation via gate; AST tables at execution repair
- [x] `EvalReadOnlySqlExecutor` via gate (EnforceReadOnly forced, MaxRows = MaxRowLimit)
- [x] `McpEvalService` ctor swap
- [x] Five fixtures: helpers only (SC8)
- [x] Checkpoint: build + 5 fixtures green + `git diff --stat` confined to helpers

## B4 — MCP callers (W2)
- [x] `ProjectQueryTool`: gate with catalog, blocking schema, AST tables, FinalSql
- [x] `DryRunTool`: gates 1-3 via gate; `read_only` verdict + code; schema skipped codes
- [x] `CrossSourceQueryService`: gate for source / repair / join; `MaxRowLimit`
- [x] `SqlParsingHelper.ExtractTableNamesFromSql` removed
- [x] Tests: `DryRunToolTests` (g), `McpPlaygroundServiceTests`, `ReadOnlyExecutionRoutingTests` ctors; new `ProjectQueryToolSchemaGateTests` (SC4)
- [x] Checkpoint: build + 4 fixtures green; SC1 + SC6 greps clean

## Final
- [x] Full `dotnet test` vs Phase 2.9 baseline (811/5/816) (SC7: 5 inherited env-red harness tests)
- [x] Behavioural diff written (sidecar implement.behavioral_diff)
- [x] Spec-drift check clean

## Post-implementation review
- [x] Stage 1 `compliance-reviewer` against sealed spec (NEEDS_CHANGES → fixed)
- [x] Stage 2 `test-reviewer`, `architecture-reviewer`, `silent-failure-hunter` (MAX) — 1 iteration
- [x] Adversarial pass on gate + rewriter (lesson 2026-07-03) — OFFSET-without-FETCH and derived-subquery gaps fixed
- [x] Audit + signal on every early exit (§1.7/§9.5); no SQL in logs (§1.11) — confirmed by compliance lane
- [x] Update parent plan Wave 0.1 status

---

# Todo — MCP `ask` SQL correctness (2026-09-04)

**Scope:** new-feature · security_impact: new-query-surface · **Rigor: MAX** (score 38 — 8 batches, 53 files, 8 external contracts, security +3)
Spec: `docs/specs/2026-09-04-ask-sql-correctness.md`
Plan: `docs/plans/2026-09-04-ask-sql-correctness.md`
Branch: `feature/verified-semantic-grounding`

## Batch 1 — Shared AskSqlPipeline + eval parity
- [x] Orchestrator pre-step: `git mv` SqlSchemaValidator MCP → Core/Services/Validation, namespace + public
- [x] `IAskSqlExecutor` + `AskExecutionResult`; `IAskSqlPipeline` + options/outcome/repair-step records
- [x] `AskSqlPipeline`: move generate→validate→repair→execute core verbatim; outcome fields replace signal calls
- [x] MCP `AskSqlExecutor` adapter; DI moves (validator → Core, pipeline → AI, executor → MCP)
- [x] `ProjectAskTool` thin wrapper maps outcome → signal in the original order; text byte-identical
- [x] `EvalReadOnlySqlExecutor`; `McpEvalService` runs the pipeline; failure tag from final SQL tables
- [x] Tests re-targeted (repair flow, voting, schema validator, eval judge gate, replay verifier) + `AskSqlPipelineTests` (parity, no MCP reference)
- [x] Checkpoint: build + filtered tests

## Batch 2 — Date anchor, dialect rule, token cap
- [x] `TimeProvider?` optional ctor param, `TODAY (UTC)` line in generation + repair messages
- [x] Dialect rule in default system prompt; `MaxTokens` 2048 ×3
- [x] `SqlGenerationPromptTests`
- [x] Checkpoint

## Batch 3 — Assumptions / clarification block
- [x] `SqlGenerationResult` + `Assumptions`, `ClarificationHint` (defaulted)
- [x] Leading-comment parser on generation and repair; prompt rule replaces "ONLY the SQL"
- [x] Pipeline outcome + `### Assumptions` / clarification rendering
- [x] Tests (parser cases, rendering)
- [x] Checkpoint

## Batch 4 — Settings + SampleValuesComplete + dual migration
- [x] 4 settings on entity/data/provider/handler with defaults true/12/true/2
- [x] `ColumnMetadata.SampleValuesComplete`, DTO default, metadata service 3 sites
- [x] PG migration scaffolded, `defaultValue` edited to code defaults; SqlServer hand-written (+Designer, snapshot)
- [x] `AskCorrectnessSettingsTests`
- [x] Checkpoint

## Batch 5 — Complete value domains
- [x] Sampler: candidate rule, per-engine DISTINCT probe via `SqlIdentifierGuard`, ≤12 → complete, PII screen, fail-soft
- [x] Extract `PiiValueScreen` (public, Core/Services/Security); sampler delegates
- [x] `SchemaColumn.SampleValuesComplete` through every projection; formatter `Values (all N)`
- [x] Prompt rule for complete values
- [x] Tests (sampler per engine, thresholds, formatter)
- [x] Checkpoint

## Batch 6 — Ask-time value grounding
- [x] `IValueGroundingService` / `ValueGroundingService` (extract, rank, sanitise, AST-gated capped probes, render)
- [x] Injected in both `GetSmartContextForAskAsync` paths before golden block; fail-closed; setting-gated; Api skipped
- [x] Prompt rule; DI registration; 5 test ctors fixed
- [x] `ValueGroundingServiceTests`
- [x] Checkpoint

## Batch 7 — Semantic linter + lint repair
- [x] `SqlSemanticLinter` (3 rules), `SchemaLintContext`, `SqlLintFinding`; Core DI
- [x] `SmartSchemaContext.PrimaryKeyCatalog`; fast path `JoinPaths`
- [x] Pipeline lint hook: ≤1 repair, accept only if valid and strictly fewer findings; `### Semantic warnings`
- [x] `SqlSemanticLinterTests` + pipeline lint tests
- [x] Checkpoint

## Batch 8 — Self-consistency gating, concurrency, tie-break
- [x] Single candidate first; gate on `SelfConsistencyMinTables`; N-1 extra candidates
- [x] `GenerateCandidatesAsync` concurrent via `Task.WhenAll`
- [x] `SelectMajority` tie-break by lint count
- [x] `SelfConsistencyVotingTests` extended
- [x] Checkpoint

## Post-implementation
- [x] Full `dotnet build --property WarningLevel=0` + `dotnet test` (baseline: 504/509, 5 inherited API-harness failures)
- [x] Phase 3.5 spec-drift check vs sidecar
- [x] Phase 4 Stage 1 compliance-reviewer; Stage 2 test-reviewer + architecture-reviewer + silent-failure-hunter
- [x] Phase 5 fix findings (≤3 iterations); Phase 6 simplify; Phase 7 lessons

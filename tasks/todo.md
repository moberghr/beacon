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

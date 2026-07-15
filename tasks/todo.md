# Todo — Bug-hunt fixes (2026-07-14)

**Scope:** bug-fix · security_impact: pii · **Rigor: HIGH** (score 9 — 3 batches, 10 files, security_impact=pii)
Spec: `docs/specs/2026-07-14-bughunt-fixes.md`
Plan: `docs/plans/2026-07-14-bughunt-fixes.md`
Branch: `fix/kb-pgvector-and-warp-bugs`

## Batch 1 — Core correctness (no external surface)
- [x] Fix 3+4: `QueryGuardrailService.ApplyRowLimit` — first-SELECT-only TOP; route AzureSynapse through the T-SQL branch
- [x] Fix 5: `SubscriptionValidator.ValidateParameters` — require each placeholder matched exactly once
- [x] Fix 2: `DataSourceService.DeleteDataSource` — `.IgnoreQueryFilters()` on the QuerySteps→Query load
- [x] Tests: `QueryGuardrailServiceTests` (subquery + AzureSynapse), new `SubscriptionValidatorTests`
- [x] Checkpoint: build 0 err; 35 guardrail+validator tests pass

## Batch 2 — PII masking on the MCP query surface (security)
- [x] Fix 1a: `ProjectQueryTool` — mask `result.Rows` via `MaskPiiValues` before formatting
- [x] Fix 1b: `CrossSourceQueryService` — mask each source's rows before loading into SQLite
- [x] Fix 1c: `QueryExecutionService` — inject `IMcpSettingsProvider`; detect + mask before formatting (ask path)
- [x] Checkpoint: build 0 err (tool paths verified via build + review; full suite after Batch 3)

## Batch 3 — LLM provider null-safety
- [x] Fix 6: `OpenAiProvider` + `AzureOpenAiProvider` — `Content?.FirstOrDefault()?.Text ?? string.Empty`
- [x] Checkpoint: build 0 err; full suite 404 pass / 0 fail

## Gate sequence — COMPLETE
3 batches ✓ → Phase 3.5 drift clean ✓ → Stage 1 compliance ✓ → Stage 2 test + architecture ✓ → Phase 6 cleanup ✓ → Phase 7 compound ✓ (2 lessons + pre-commit item 7 extended)

## Post-implementation review items
- [x] Full `dotnet test` green — 404 pass / 0 fail
- [x] Behavioral diff written
- [x] Phase 3.5 drift clean — files match manifest; no committed migration edited
- [x] Stage 1 compliance — 3 Warnings, all fixed (CTE row-limit regression + test; archive idempotency guard; PII masking wiring test)
- [x] Stage 2 test-reviewer (approved_with_warnings) + architecture-reviewer (1 Critical)
- [x] Fix findings (iteration 1): arch-F1 recompute PII from executed SQL in CrossSource (Critical); arch-F2 move IsTSqlEngine last; test-F3 branch-specific message assertions; test-F4 Synapse CTE test
- [x] Waived: test-F1 (DeleteDataSource — NRE needs real EF provider; §4.7 forbids InMemory; double can't run Include/IgnoreQueryFilters); test-F2 (ProjectQueryTool masking — below threshold, identical to tested QueryExecutionService pattern, disproportionate DI)
- [x] Cleanup: changes already minimal; arch-F3 (shared MaskRows helper) deferred — would expand scope into SemanticSearchService
- [x] Final: build 0 err; full suite 405 pass / 0 fail

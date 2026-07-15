# Todo — Self-Learning Loop Tier 2 (2026-07-10)

**Scope:** new-feature · security_impact: pii-exposure · **Rigor: MAX (score ~20)**
Spec: `docs/specs/2026-07-10-self-learning-tier2.md`
Plan: `docs/plans/2026-07-10-self-learning-tier2.md`
Branch: `feature/kb-selflearning-tier0-1` (continues Tier 0/1)

> Tier 0/1 (2026-07-09) is complete + verified (build 0 err, 324 tests) and remains in the working tree as Tier 2's base. Its todo is preserved at `docs/plans/2026-07-09-kb-selflearning-tier0-1.md`.

## Batches

- [x] **T2-B1 — Status + validity schema:** `McpPatternStatus.NeedsEvidence`; `McpLearnedPattern.SupersededAt`/`LastVerifiedAt`; 2 settings + mapping; dual migration + snapshots (no EF drift, defaults manual) ✓ build 0 err, 324 tests pass
- [x] **T2-B2 — LLM lesson extraction (⑦):** `ILessonExtractor` (Core) + `LlmLessonExtractor` (AI, queue-backed, defensive parse, OCE rethrow, cluster-text only); `DetectSchemaCorrectionsAsync` LLM-primary + regex fallback; 7 tests ✓ build 0 err, 331 tests pass
- [x] **T2-B3 — Replay-verification gate (⑥):** `IPatternReplayVerifier` + impl (relevant failing cases by DataSourceId+GoldSql table match); factored `GenerateExecuteCompareAsync` (read-only reused, extraContext hook) + `EvaluateCasePassesAsync`; promotion `NeedsEvidence`→replay→`AutoApproved`; mutating-gold-never-executed proven; 8 tests ✓ build 0 err, 339 tests pass
- [x] **T2-B4 — Retrieval selection + decay (⑧):** embed all approved patterns (not just CommonQuery, skip stale); all-type top-k semantic selection + `SupersededAt==null` filter in `GetRelevantPatternsAsync`; `DetectStalePatternsAsync` (column-gone → SupersededAt, history kept); 3 tests ✓ build 0 err, 342 tests pass

## Gate sequence
4 batches → Phase 3.5 drift check → Stage 1 compliance-reviewer → Stage 2 [test-reviewer + architecture-reviewer + silent-failure-hunter] → Phase 6 cleanup → Phase 7 compound

## Post-implementation review items
- [x] Full `dotnet test` green — 349 passed / 0 failed
- [x] Phase 3.5 drift clean — no committed migration edited; all files in manifest
- [x] Stage 1 compliance-reviewer — PASS (no Critical/Warning)
- [x] Stage 2 (MAX): architecture APPROVED; silent-failure + test reviews triaged
- [x] Replay execution proven read-only (mutating candidate/gold never executed — test)
- [x] No auto-approval without measured evidence (NeedsEvidence default; confidence-can't-promote test)
- [x] LLM extractor: failure-cluster text only, via queue, OCE rethrow; regex fallback works offline (tested)
- [x] Migrations: defaults on both providers; no committed migration edited
- [x] Phase 5 (2 iterations): silent-failure F1-F4 fixed (deterministic replay, Measurable flag, Errored verdict, stale-vector prune) + visibility (null-verifier warn, OCE loop-boundary, persistent-extraction-failure signal); test findings F1-F4 fixed (cancellation tautology, fallback-wiring, promotion-loop isolation, lesson-block content)

## Deferred follow-ups (documented, not done this session)
- [ ] **Live replay measurement** (needs golden set + live DB + model): actually run the gate. Code-complete + unit-tested with mocked eval. Safe default holds: no auto-approval without measured evidence.
- [ ] **Replay determinism at scale**: temp-0 generation is in; once live, consider best-of-N agreement + `LearningReplayMinFlips >= 2` to further de-noise flips.
- [ ] Tier 2.5 (GEPA/DSPy trace-compiled prompt optimization) + Tier 3 (chunking, contextual retrieval, glossary, semantic layer).

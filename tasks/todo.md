# Todo — Knowledge-Base Tier 3 (2026-07-12)

**Scope:** new-feature · security_impact: pii-exposure · **Rigor: MAX**
Spec: `docs/specs/2026-07-12-kb-tier3.md`
Plan: `docs/plans/2026-07-12-kb-tier3.md`
Branch: `feat/warp-jobs` (Tier 0/1/2 + Warp/net10 base)

## Batches

- [x] **T3-B1 — Schema & settings:** OwnerType +DocChunk/GlossaryTerm; `McpEmbedding.ProjectId` (+ raw NN SELECT fix); `McpDocChunk` + `McpGlossaryTerm`; 5 settings + mapping; dual migration + snapshots ✓ build 0 err, 349 tests pass
- [x] **T3-B2 — Chunker + NN generalization:** pure `DocumentChunker` (sentence-window, 12 tests); NN helpers take `int? projectId` (existing call sites → null, no regression); DocChunk/Glossary owner-type constants ✓ build 0 err, 361 tests pass
- [x] **T3-B3 — Doc-chunk indexing + contextual retrieval (⑨+⑩):** `IDocChunkIndexingService`/impl (chunk→opt LLM blurb→embed→upsert+prune, gated, OCE rethrow); `IKnowledgeGraphService.GetRelevantDocChunksAsync`; `KnowledgeAnswerService` top-K vs truncation fallback; `ReindexDocChunksJob` Warp job; 5 tests ✓ build 0 err, 366 tests pass
- [x] **T3-B4 — Glossary (⑪):** 4 admin CRUD handlers + `GlossaryEndpoints` (all mapped); glossary embedding in reindex (soft-deactivate + prune); top-K glossary injection into `GetSmartContextForAskAsync` (both paths, projectId resolved from dataSourceId); 9 tests ✓ build 0 err, 375 tests pass

## Gate sequence
4 batches → Phase 3.5 drift check → Stage 1 compliance-reviewer → Stage 2 [test-reviewer + architecture-reviewer + silent-failure-hunter] → Phase 6 cleanup → Phase 7 compound

## Post-implementation review items
- [x] Full `dotnet test` green — 387 passed / 0 failed on net10
- [x] Phase 3.5 drift clean — no committed migration edited; all files in scope
- [x] Stage 1 compliance — 1 Warning (index/query embedding asymmetry) FIXED + guard test
- [x] Stage 2 (MAX): architecture approve_with_findings; test + silent-failure findings fixed (Phase 5)
  - SF2 fail-closed on both new retrieval paths; SF1 blurb-failure Error signal; A1 IJobService facade; A2 DataSourceId nullable; F001/F002/F003 tests added; F004 exact-topK + XML doc fix
- [x] New Warp job routed through IJobService facade (not Hangfire); recurring registration correct
- [x] Contextual blurb sends only section text; gated + LLM via queue; blurb-failure → raw chunk + Error signal
- [x] Glossary endpoints admin-policy gated; all 4 handlers mapped
- [x] Existing B5/B6 retrieval behaviour unchanged (NN generalization additive)
- [x] Migration live-verified: full chain + amended AddKbTier3 (DataSourceId nullable) applies on PG17/pgvector 0.8.5; both Tier-3 tables created
- [ ] Live recall over real docs / real ONNX blurb generation → infra follow-up (unit-tested with fake embedder + mocked LLM)
- [ ] Deferred: ⑫ semantic layer, Tier 2.5 GEPA/DSPy, contextual-BM25, cross-encoder reranker
- [ ] Follow-up (below-threshold): KnowledgeGraphService growing (1440→1684 lines) — candidate to split a RetrievalService if a 4th owner type is added

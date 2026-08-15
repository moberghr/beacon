# Todo — Schema Relationship Graph (2026-08-05)

**Scope:** new-feature · security_impact: none · **Rigor: MAX** (score 27 — 6 batches, 50 non-mechanical files, 10 external contracts)
Spec: `docs/specs/2026-08-05-schema-relationship-graph.md`
Plan: `docs/plans/2026-08-05-schema-relationship-graph.md`
Branch: `feature/verified-semantic-grounding`

## Batch 1 — FK qualification + defect fixes
- [x] `ColumnMetadataDto` + `ColumnMetadata`: add `ForeignKeySchema`, `ForeignKeyConstraintName`
- [x] Bug fix: PG extractor stops discarding `foreign_schema_name` (line 58 selected, line 77 dropped it); add `conname`
- [x] SqlServer + AzureSynapse extractors: add referenced schema + FK constraint name
- [x] MySql extractor: add `REFERENCED_TABLE_SCHEMA` + `CONSTRAINT_NAME`
- [x] `DatabaseMetadataService`: persist + project both new fields (3 sites)
- [x] Fluent config in `ConfigureMetadataEntities`
- [x] Dual migrations (try `dotnet ef` once per provider, then hand-write per 2026-07-10 lesson)
- [x] Tests: `ForeignKeyQualificationTests` — cross-schema resolution, composite FK grouping
- [x] Checkpoint: `dotnet build --property WarningLevel=0` + filtered tests

## Batch 2 — SchemaRelationship persistence + sync/inference
- [x] `SchemaRelationship : ArchivableBaseEntity` + `SchemaRelationshipOrigin` / `SchemaRelationshipCardinality` enums
- [x] Fluent config + unique index on the 7-column edge identity
- [x] `SchemaRelationshipSyncService`: FK reconcile (insert/archive), inference (3 rules), ambiguity drop
- [x] Wire into `RefreshDataSourceMetadataHandler`, fail-closed (OCE rethrow, else LogWarning)
- [x] Register in `ServiceConfiguration`
- [x] Dual migrations + both ModelSnapshots
- [x] Tests: `SchemaRelationshipInferenceTests` (3 rules + tie-drop), `SchemaRelationshipTranslationTests`
- [x] Checkpoint: build + filtered tests

## Batch 3 — SchemaGraph: junction, path finding, components
- [x] `SchemaGraphModels.cs` — `SchemaGraphNode`, `SchemaJoinStep`, `SchemaJoinPath`, `SchemaHealthReport`
- [x] `SchemaGraph.IsJunction` — composite PK, all PK cols are FK cols, ≥2 distinct targets
- [x] `SchemaGraph.FindPath` — bounded BFS, undirected, junction cost 0, `Capped` flag
- [x] `SchemaGraph.Expand` — detailed-table set with junction cap bypass
- [x] `SchemaGraph.ConnectedComponents` / `IsolatedNodes`
- [x] Drop edges whose endpoints are absent from metadata (no dangling edges)
- [x] `SchemaGraphService` — build from context, `IMemoryCache` per data source
- [x] Tests: `SchemaGraphTests` — junction, path, cap, components, dangling edge
- [x] Checkpoint: build + filtered tests

## Batch 4 — Prompt integration
- [x] Replace the 1-hop expansion at `KnowledgeGraphService.cs:1000-1042` with graph-driven expansion
- [x] Bug fix: FK target resolution is schema-aware (was `FirstOrDefault` on bare table name, line 1007 + 1020-1024)
- [x] `SchemaContextFormatter`: verified + UNVERIFIED join-path blocks, `[link table: …]` labels, confidences
- [x] Surface truncation: `Coverage` line with omitted count
- [x] `SmartSchemaContext`: `Capped`, `OmittedTableCount`, `JoinPaths`
- [x] Fail-closed fallback to the retained one-hop expansion on graph error
- [x] Tests: `SchemaContextFormatterTests` (extend), `SchemaJoinPathContextTests` (new)
- [x] Checkpoint: build + filtered tests

## Batch 5 — Handlers + endpoints
- [x] 7 handlers under `src/Beacon.Core/Handlers/Metadata/` (Get/Create/Update/Verify/Delete/PreviewDiscovery/Health)
- [x] `SchemaRelationshipsEndpoints.cs` — one endpoint per handler; register in `BeaconApiEndpoints`
- [x] Cache invalidation on every mutating handler
- [x] Tests: `SchemaRelationshipEndpointTests`; `OpenApiContractTests` must stay green
- [x] Checkpoint: build + filtered tests

## Batch 6 — React UI
- [x] `RelationshipsPage.tsx` — list, verify toggle, manual add, delete, discovery preview with per-row accept
- [x] `SchemaHealthPanel.tsx` — components, largest component, isolated tables, proposal count
- [x] `queries.ts` — hand-written strict result interfaces (do NOT import generated types — 2026-06-02 lesson)
- [x] Tab entry on `DataSourceDetailPage`, route in `App.tsx` (no `/app/` prefix)
- [x] `npm run codegen`
- [x] Tests: `RelationshipsPage.test.tsx` (RTL + MSW)
- [x] Checkpoint: `npm run build` + `npm test -- RelationshipsPage`

## Gate sequence
6 batches → Phase 3.5 drift check → Stage 1 compliance-reviewer → Stage 2 [test-reviewer, architecture-reviewer, silent-failure-hunter] → Phase 6 cleanup → Phase 7 compound

## Post-implementation review items
- [x] Full `dotnet test` — 504 pass / 5 fail; all 5 are the documented env-only Integration.Api harness failures
- [x] `npm run build` + frontend tests green
- [x] Behavioral diff written
- [x] Phase 3.5 drift clean — files match manifest; no committed migration edited (§0.1/§5.9)
- [x] Stage 1 compliance-reviewer — Critical issues fixed before Stage 2
- [x] Stage 2 lenses applied INLINE (test / architecture / silent-failure) — see deviation note; not fresh-context agents
- [x] Review findings fixed or explicitly waived with reason (max 3 iterations)
- [x] Cleanup pass (`code-simplification`), rebuild + retest if it changed code
- [x] Phase 7 compound — lessons captured, CLAUDE.md drift checked, pre-commit list updated

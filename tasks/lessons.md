# Lessons Learned

## NSwag generates intentionally loose types — local strict result interfaces are deliberate (2026-06-02)

**What happened:** A "refactor" to delete duplicated result interfaces and import generated types (to drop ~92 `as unknown as` casts) was premised on the casts being gratuitous. They are not.

**Rule:** Do NOT replace hand-written result/command interfaces in `src/Beacon.UI/web/src/routes/**/queries.ts` with imports from `src/api/generated/beacon-api.ts`. The NSwag config (`nswag.config.json`) sets `markOptionalProperties: true`, emits a `[key: string]: any` index signature on all 223 interfaces, and types `DateTime` as `Date` even though the client deserializes with a plain `JSON.parse` (no reviver) so dates are strings at runtime. The local interfaces are stricter and more correct.

**Instead:** bridge the loose generated payload into the strict local type at the call boundary via `unwrap<T>()` in `src/lib/api.ts` (the single, greppable trust boundary; add zod here later if needed). The "92 casts" were 5 unrelated categories — only the ~40 named-result-type double-casts are the addressable ones; `as never` command args, react-hook-form `register('x' as never)`, Monaco, and Date-field casts are legitimate and unrelated.

**Why it matters:** Importing the generated types would explode `tsc` errors (optional everywhere, Date vs string) and *reduce* type quality. Verify codegen output shape before assuming duplication is debt.

**When it applies:** Any time generated-client types look "duplicated" by local interfaces in this repo.

## Beacon.Tests has no global usings — new test files need `using NUnit.Framework;` (2026-06-10)

**What happened:** Four new test files failed to compile with `TestFixtureAttribute could not be found`.

**Rule:** Every new test file in Beacon.Tests must explicitly include `using NUnit.Framework;` — the project does not define global usings for the test framework.

**Why it matters:** The error surfaces only at `dotnet test` (the main solution build skips the test project), so it's easy to claim a green build prematurely.

**When it applies:** Any new file under src/Beacon.Tests/.

## Mocking internal interfaces needs InternalsVisibleTo for DynamicProxyGenAssembly2 (2026-06-10)

**What happened:** Moq threw "not accessible to the proxy generator" when mocking the internal `IQueryExecutionService` from Beacon.MCP, even though `InternalsVisibleTo("Beacon.Tests")` was present.

**Rule:** When a project exposes internal interfaces that tests mock with Moq, the csproj needs BOTH `<InternalsVisibleTo Include="Beacon.Tests" />` AND `<InternalsVisibleTo Include="DynamicProxyGenAssembly2" />`.

**Why it matters:** The first IVT covers compile-time access; Castle DynamicProxy generates the mock in its own assembly at runtime and needs its own grant.

**When it applies:** Beacon.MCP and Beacon.AI now have both; apply the same pair to any other project whose internals get mocked.

## EF migrations ignore C# property initializers — bool defaults scaffold as false (2026-06-10)

**What happened:** `EnableSampleValueCollection { get; set; } = true;` scaffolded a migration with `defaultValue: false`, which would have silently disabled the feature for every existing installation on upgrade.

**Rule:** After scaffolding a migration that adds a non-nullable column whose entity initializer is non-default (e.g. bool `= true`), check the generated `AddColumn` and set `defaultValue:` manually to match the intended backfill — in BOTH provider migrations. Property initializers are invisible to EF model building.

**Why it matters:** The default controls the backfill for existing rows; getting it wrong flips behavior for upgraders only, which no test catches.

**When it applies:** Every dual-provider migration adding non-nullable columns with non-default intended values.

## `dotnet format` can apply the EF1002 codefix and break DDL (2026-06-10)

**What happened:** A format pass rewrote `ExecuteSqlRaw($"CREATE SCHEMA {schema};")` to `ExecuteSql(...)`, which parameterizes the interpolation — `CREATE SCHEMA @p0` is invalid SQL and throws at runtime.

**Rule:** After running `dotnet format`, diff-review any `ExecuteSqlRaw`→`ExecuteSql` rewrites. DDL identifiers cannot be parameters; keep `ExecuteSqlRaw` with an identifier whitelist check (see ServiceConfiguration.UseBeacon) and a scoped `#pragma warning disable EF1002`.

**Why it matters:** The change compiles cleanly and only fails when the code path runs (here: first-run schema creation).

**When it applies:** Any raw-SQL DDL with interpolated identifiers.

## Known pre-existing test failure: AuthPermissions_Anonymous_Returns401Json (2026-06-10)

**What happened:** `src/Beacon.Tests/Integration/Api/Phase1HarnessTests.cs` fails with "No authenticationScheme was specified, and there was no DefaultChallengeScheme found" — verified failing on clean HEAD via a throwaway worktree.

**Rule:** Treat this single failure as the known baseline until the harness registers a default challenge scheme; do not block unrelated merges on it, and do not silently include a fix in unrelated work.

**When it applies:** Interpreting `dotnet test` results on this repo (expect N-1 passes until fixed).

## A "read-only" AST validator needs to be re-attacked from every angle SQL offers a side door (2026-07-03)

**What happened:** `SqlReadOnlyAstValidator` (added to gate query-builder steps and 5 SQL connectors against writes) shipped with two separate bypasses found only at review time, not at implementation time: (1) a data-modifying CTE — `WITH x AS (INSERT ... RETURNING id) SELECT * FROM x` — parses as `Statement.Select` and passed unchecked; (2) `EXPLAIN ANALYZE INSERT/UPDATE/DELETE ...` parses as `Statement.Explain` and was accepted unconditionally without inspecting the wrapped statement — on PostgreSQL/MySQL/Databricks this actually executes the write. Both were caught by dedicated adversarial review passes (`compliance-reviewer`, then `silent-failure-hunter`), not by the implementer or by initial test-writing.

**Rule:** When adding an AST-based SQL allow-list gate (SELECT-only, read-only enforcement), explicitly enumerate and test every AST node type that can WRAP or CONTAIN another statement, not just the top-level statement type: CTEs (`WITH`), set operations (`UNION`/`INTERSECT`/`EXCEPT`), parenthesized subqueries, and `EXPLAIN`/`EXPLAIN ANALYZE`. A validator that checks only `statement is Statement.Select` and stops is not done — walk the whole tree recursively. Also: flip "parser can't parse it" to fail-closed (reject), not fail-open (allow) — the fail-open assumption is often inherited from a context where a different validator was the actual authority and stops being safe the moment this validator becomes the sole gate somewhere else.

**Why it matters:** Both bugs were real, exploitable fail-opens in a security control this repo explicitly ships to prevent writes through the query-builder and SQL connectors. Neither would have been caught by "does it compile and pass the happy-path test" — they required someone deliberately trying to think like an attacker with SQL knowledge of parser edge cases.

**When it applies:** Any time a new or modified SQL-parsing/allow-list validator is added anywhere in Beacon (MCP tools, connectors, query builder). Before considering such a validator done, explicitly test: CTEs wrapping DML, UNION arms hiding a mutation, EXPLAIN wrapping DML, and parse-failure behavior. Route it through at least one adversarial review pass (`compliance-reviewer` and/or `silent-failure-hunter`) before merge — this class of bug reliably survives a first implementation pass.

## Hand-write dual migrations here — `dotnet ef` scaffolding is unreliable in this repo (2026-07-10)

**What happened:** Adding 3 dual-provider migrations (settings, McpEmbedding, McpEval), the composition root (`src/Beacon.SampleProject/Program.cs`) wires **PostgreSQL only** at design time (`.UsePostgreSql(...)`), and there is **no `IDesignTimeDbContextFactory`**. So `dotnet ef migrations add --project src/Beacon.Core.SqlServer` cannot resolve `SqlServerBeaconContext` without a fragile temporary provider-switch, and the pgvector `vector(384)` column + HNSW index can't be scaffolded by EF at all.

**Rule:** For dual-provider schema changes, hand-write BOTH migrations into `src/Beacon.Core.{PostgreSql,SqlServer}/Data/Migrations/` (timestamp-prefixed name AFTER the latest existing, plus the `.Designer.cs` companion) AND update BOTH `*BeaconContextModelSnapshot.cs` — do not rely on `dotnet ef migrations add`. Prefer `.HasDefaultValue(...)` in the fluent config so model==snapshot==migration (no EF drift) and set `defaultValue:` on every non-nullable `AddColumn` (see the 2026-06-10 initializer lesson). PG-only extension columns (pgvector) go in via `migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS vector; ALTER TABLE ... ADD COLUMN embedding vector(384);")` + a raw HNSW index — kept OUT of the EF model/snapshot.

**Why it matters:** tests here never apply migrations (`NpgsqlTestContext.ToQueryString()`, no live DB), so a broken/omitted migration passes build+test and only fails at a real deploy. Hand-writing is deterministic; scaffolding silently targets the wrong context or drops the vector DDL.

**When it applies:** any dual-provider entity/column change in Beacon.

## Trust `dotnet build`, not the LSP, after adding NuGet packages or editing shared files (2026-07-10)

**What happened:** Repeatedly during this build the C# LSP reported `CS0234 'Microsoft.ML' does not exist` / `CS0246 InferenceSession not found` / `CS1061 IBeaconScheduler has no EnqueueMcpEval` on files that compiled cleanly — the LSP workspace had not re-restored the newly-added `Microsoft.ML.OnnxRuntime`/`Pgvector` packages or re-parsed a just-modified interface. `dotnet build --property WarningLevel=0` reported 0 errors in every case.

**Rule:** When LSP diagnostics contradict a fresh `dotnet build`, the compiler is authoritative. After adding a package reference or editing a widely-referenced file, verify with `dotnet build` and treat stale `CS0234/CS0246/CS1061/CS8933/CS8019` LSP noise on those files as non-blocking. (The `CS8933`/`CS8019` "duplicate/unnecessary global using" warnings on EF-generated snapshot/migration files are inherent to those files and benign at `WarningLevel=0`.)

**When it applies:** any change that adds NuGet packages or touches a file the LSP has cached.

## Keep provider-specific vector types out of provider-neutral Core (2026-07-10)

**What happened:** Adding pgvector: the shared abstract `BeaconContext` and the `McpEmbedding` entity must stay provider-neutral (§5.2/§5.4). Putting a `Pgvector.Vector` property on the entity would force the `Pgvector` package into `Beacon.Core` and break neutrality.

**Rule:** Store embeddings as `byte[]` on the entity (works as `bytea`/`varbinary` on both providers); add the Postgres `vector(384)` column + HNSW index via raw migration SQL only (DB-managed, not an EF property); do vector search with a provider branch on `context.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL"` (string compare, NOT `IsNpgsql()`, to keep `Beacon.AI` off the Npgsql package) → `FromSqlInterpolated` with a parameterized `'[...]'::vector(384)` literal on PG, in-memory `EmbeddingCodec.Cosine` elsewhere. `Pgvector`/`Pgvector.EntityFrameworkCore` belong ONLY in `Beacon.Core.PostgreSql.csproj`.

**When it applies:** any future embedding/ANN work; any provider-specific column type on a shared entity.

## A measurement/eval harness must exclude infra failures from its headline metric (2026-07-10)

**What happened:** `McpEvalService` initially counted any thrown exception (LLM outage, DB blip, missing data source) as `ExecutionError` and folded it into `ExecutionAccuracy = passed / totalCases` — so an outage mid-run silently deflated the reported accuracy, defeating the harness's whole purpose.

**Rule:** In an eval/scoring harness, distinguish "could not evaluate" (harness/infra/generation exception) from "evaluated and wrong." Tag the former separately (`McpEvalFailureTag.HarnessError`) and score accuracy ONLY over evaluated cases (`passed / (total - errored)`), surfacing the excluded count. A wrong headline number is worse than a smaller-but-honest one.

**When it applies:** any batch scorer/eval loop where per-item work can fail for reasons unrelated to what's being measured.

## A non-awaited async assertion is a test that CANNOT fail (2026-07-10)

**What happened:** A Tier-2 cancellation test `public void ExtractAsync_Cancellation_...()` did `var act = async () => ...; act.Should().ThrowAsync<OperationCanceledException>();` WITHOUT awaiting. The returned `Task<ExceptionAssertions>` was discarded and NUnit finished the synchronous `void` method before the assertion ran — a reviewer empirically proved it still passed against an implementation that swallowed the exception. Zero verification value for the one regression it guarded.

**Rule:** Any FluentAssertions `*Async` assertion (`ThrowAsync`, `NotThrowAsync`, `CompleteWithinAsync`) MUST be `await`ed, and the test method MUST be `public async Task` (never `void`/`async void`). A bare non-awaited `ThrowAsync` on a `void` test is an always-green tautology. When reviewing tests, grep changed test files for `ThrowAsync`/`NotThrowAsync` not preceded by `await`, and for `async void`.

**Why it matters:** a false-confidence test is worse than no test — it actively signals "covered" for a behavior that is unguarded. This class survives a green `dotnet test` run and only a mutation/adversarial check catches it.

**When it applies:** every async NUnit test in Beacon.Tests using FluentAssertions.

## Making a private method `internal` for testing? Bump every private type in its signature too (2026-07-10)

**What happened:** Promoting `DetectSchemaCorrectionsAsync` from `private` to `internal` (so a test could call it) compiled-errored `CS0051` because a parameter type (`ExtractionStats`, a `private sealed` nested class) was less accessible than the now-internal method.

**Rule:** When widening a method's accessibility (private→internal) to test it, every type in its signature must be at least as accessible. Bump the nested helper types (`ExtractionStats` here) to `internal` in the same edit. `InternalsVisibleTo("Beacon.Tests")` then makes both reachable.

**When it applies:** exposing internals for unit tests anywhere in Beacon.

## A replay/measurement gate must generate DETERMINISTICALLY and separate "unmeasurable" from "no-improvement" (2026-07-10)

**What happened:** The Tier-2 replay-verification gate promotes a learned pattern if injecting it flips ≥N failing eval cases. Two silent-correctness bugs found in review: (1) generation used a non-zero temperature (0.1) for both baseline and candidate passes, so a single "flip" could be sampling NOISE, not the lesson's effect — auto-promoting a useless pattern; (2) an infra error (DB blip / LLM outage) returned `Success=false`, which the gate counted as a legitimate baseline-fail (→ false flip) or candidate-fail (→ false block), and an all-errored run produced a verdict byte-identical to "baseline already passes everything" — a good pattern stuck forever with no signal.

**Rule:** For any measured promotion/gate that re-generates via an LLM: (a) generate at temperature 0 on the measurement path so a flip reflects the change under test, not sampling variance (and/or require best-of-N agreement + a min-flips bar > 1); (b) thread a `Measurable` flag (did BOTH sides actually execute?) so an infra `Success=false` is counted as *errored*, never as a clean pass/fail; (c) carry an explicit `Errored` count in the verdict and require `measured > 0` to pass; (d) log the full verdict breakdown so "couldn't measure" is operationally distinct from "measured, didn't help." Never let confidence alone auto-approve — that's the memory-poisoning surface the gate exists to close.

**When it applies:** the replay gate and any future measured-promotion loop (Tier 2.5 GEPA/DSPy, A/B lesson gating).

## Index-time and query-time embedding MUST use the same representation (2026-07-13)

**What happened:** Tier-3 doc-chunk retrieval (`GetRelevantDocChunksAsync`) embedded `Mask(question)` while chunks were embedded RAW at index time. Masking (strip literals/numbers → `<num>`/`<value>`) is a DAIL-SQL *exemplar* technique — it makes structurally-similar SQL questions collide. For prose RAG it puts the query vector in a different region than the raw chunk vectors, silently degrading top-K recall (the whole point of the feature). Caught by Stage-1 review, not tests (the top-K test mocked the retrieval; the indexing test used a fake embedder).

**Rule:** For any embedding retrieval, the query and the stored content MUST be transformed identically before `EmbedAsync`. Masking belongs ONLY where both sides are masked (SQL exemplars, glossary terms). For prose/doc chunks, embed both sides raw. Add a test that seeds a decoy whose vector is the *wrong* transform and asserts the correctly-transformed match wins — a mock-the-retrieval test cannot catch this.

**When it applies:** every new embedding-retrieval path in Beacon (doc chunks, glossary, future RAG).

## A best-effort enrichment added to a primary flow must FAIL-CLOSED, not propagate (2026-07-13)

**What happened:** Tier-3 added `GetRelevantDocChunksAsync` (into `KnowledgeAnswerService`'s `Task.WhenAll`) and `BuildGlossaryBlockAsync` (into `GetSmartContextForAskAsync`) without try/catch. A transient embedding/vector-store error would then fail the ENTIRE `ask`/answer — a question that answered fine before the feature existed now throws. The pre-existing sibling arms (`SearchAsync` dense arm, `GetRelevantPatternsAsync` semantic path) already fail-closed (rethrow OCE, else LogWarning + return empty/baseline).

**Rule:** When adding an optional enrichment (extra retrieval arm, injected context block) to an existing user-facing path, wrap it `catch (OperationCanceledException) { throw; } catch (Exception ex) { logger.LogWarning(...); return <empty/baseline>; }` so a failure degrades to the pre-feature behaviour with a signal — never turns a best-effort add into a hard dependency. Match the fail-closed pattern the existing arms on that path already use.

**When it applies:** any new arm added to `SearchAsync`/`GetSmartContextForAskAsync`/`KnowledgeAnswerService` or similar primary flows.

## LSP staleness includes SEMANTIC errors after an entity property type change (2026-07-13)

**What happened:** Making `McpEmbedding.DataSourceId` `int → int?` made the LSP report `CS0037 "cannot convert null to int"` at the `DataSourceId = null` initializers — a semantic error, not the usual CS8019/CS8933 using-directive noise. `dotnet build` was 0 errors: the LSP simply hadn't reindexed the property's new nullability. (Also seen: `CS0246 Warp/IJob not found` after the Warp package landed.)

**Rule:** `dotnet build --property WarningLevel=0` is authoritative over the C# LSP for Beacon — for stale-namespace (CS0246), duplicate-using (CS8933/CS8019), AND semantic (CS0037) errors that appear right after adding a package or changing an entity property's type. Always confirm with a real build before acting on an LSP error in a just-edited file; never "fix" a phantom LSP error the compiler doesn't report.

**When it applies:** any edit that adds a package reference or changes a type/nullability the LSP must reindex.

## A guardrail that DETECTS must be APPLIED at every output surface — and recomputed from the SQL that actually runs (2026-07-14)

**What happened:** `IQueryGuardrailService.ValidateQuery` returns `PiiColumns`, and `SemanticSearchService` masked rows with `MaskPiiValues(row, piiCols)` — but the MCP `query`/`ask`/cross-source surfaces (`ProjectQueryTool`, `QueryExecutionService`, `CrossSourceQueryService`) computed `PiiColumns` and then discarded it, returning raw PII to the client (§1.6/§1.11 leak). While fixing it, a second trap surfaced: `CrossSourceQueryService` runs a dry-run *repair* that replaces the SQL, so a `PiiColumns` snapshot taken from the pre-repair SQL is stale — a repaired query selecting a new PII column would ship unmasked.

**Rule:** When a guardrail computes a security decision (PII columns, read-only verdict), EVERY surface that emits rows must apply it, not just one. And compute it from the SQL that is *actually executed* — recompute after any repair/rewrite step, never reuse a snapshot taken before the SQL changed. Mirror the canonical applier (`SemanticSearchService`) exactly. If the same mask-before-emit block appears at 3+ sites, consider a single `MaskRows(rows, sql, options)` entry point so detection and masking can't drift apart (deferred here to keep scope minimal).

**When it applies:** any new query/result surface in Beacon.MCP or Beacon.AI that returns provider rows to a client, especially paths with a repair/retry loop.

## T-SQL row-limit rewriting: cap only the OUTERMOST result, and treat AzureSynapse as T-SQL (2026-07-14)

**What happened:** `QueryGuardrailService.ApplyRowLimit` had two bugs. (1) `Regex.Replace(sql, @"\bSELECT\b", "SELECT TOP N")` (no count) injected `TOP` into EVERY SELECT — subqueries/CTEs got truncated before aggregation, silently corrupting COUNT/SUM on SQL Server. (2) Only the literal `"MSSQL"` took the T-SQL branch, so `DatabaseEngineType.AzureSynapse.ToString()` fell through to `... LIMIT N`, which T-SQL rejects — every row-limited Synapse query errored.

**Rule:** For T-SQL row limits: SELECT-leading query → `TOP` on the FIRST SELECT only (`Regex.Replace(..., replacement, count: 1)`); WITH/CTE-leading query → append `ORDER BY (SELECT NULL) OFFSET 0 ROWS FETCH NEXT N ROWS ONLY` (a CTE can't be wrapped in a derived table and `TOP` can't reach the outer SELECT by regex); already-ordered query → `OFFSET/FETCH`. Route BOTH `MSSQL` and `AzureSynapse` through this branch — mirror `SqlReadOnlyAstValidator.ResolveDialect`, which already maps `azuresynapse → MsSqlDialect`. Never use `Regex.Replace` without a `count` when you mean "the first match".

**When it applies:** any engine-specific SQL rewriting in `QueryGuardrailService` or the connectors; any time a new `DatabaseEngineType` is added (check every `ToString()`-based engine switch).

## `dotnet ef migrations add` DOES work for PostgreSQL here — only SqlServer needs hand-writing (2026-08-06)

**What happened:** The 2026-07-10 lesson said to hand-write BOTH provider migrations because scaffolding is unreliable. Tested directly this session: `dotnet ef migrations add <Name> --project src/Beacon.Core.PostgreSql --startup-project src/Beacon.SampleProject --context PostgreSqlBeaconContext --output-dir Data/Migrations` **succeeds** and emits correct snake_case DDL plus the `.Designer.cs` and an updated `PostgreSqlBeaconContextModelSnapshot`. The same command for `--context SqlServerBeaconContext` fails with `Unable to resolve service for type 'DbContextOptions<SqlServerBeaconContext>'`, exactly as the earlier lesson predicted — the host wires PostgreSQL only at design time and there is no `IDesignTimeDbContextFactory`.

**Rule:** For a dual-provider schema change: **scaffold the PostgreSQL migration** (it is the wired design-time provider), then **hand-write only the SqlServer side** — migration + `.Designer.cs` + the `SqlServerBeaconContextModelSnapshot` entry. Generate the SqlServer `.Designer.cs` by copying the just-updated snapshot and swapping the class declaration to `[Migration("<ts>_<Name>")] partial class <Name>` with `BuildTargetModel`; the copy also needs `using Microsoft.EntityFrameworkCore.Migrations;` added, which the snapshot does not carry. Give the SqlServer migration a timestamp one second after the PG one so ordering is obvious. This halves the hand-writing the earlier lesson prescribed.

**Why it matters:** hand-writing the PG side wastes time and risks snake_case/type mistakes EF gets right for free.

**When it applies:** every dual-provider entity/column change in Beacon. (Supersedes the "hand-write BOTH" half of the 2026-07-10 lesson; everything else there still holds.)

## A unique index on a soft-deleted entity still covers archived rows — reconcile loops must `IgnoreQueryFilters()` (2026-08-06)

**What happened:** `SchemaRelationship : ArchivableBaseEntity` has a unique index on its 7-column edge identity. `SchemaRelationshipSyncService` loaded existing rows through the normal `DbSet` — so the global soft-delete filter hid archived rows — then inserted any edge it did not see. After a user deleted (archived) a foreign-key-derived relationship, the next metadata refresh re-derived that same edge, did not see the archived row, and inserted a duplicate → unique-constraint violation on `SaveChangesAsync`. Worse, the insert ran inside the deliberately fail-closed `try/catch` in `RefreshDataSourceMetadataHandler`, so the exception became a `LogWarning` and **relationship sync silently stopped working forever after the first delete**.

**Rule:** When a soft-deleted (`ArchivableBaseEntity`) table has a unique index on a natural key, any idempotent sync/reconcile/upsert loop MUST load existing rows with `.IgnoreQueryFilters()` and treat archived rows as *present* for de-duplication — then filter to `ArchivedTime == null` separately for the rows it intends to mutate. The global query filter protects reads; it actively lies to writers. Check this pairing whenever `HasQueryFilter` and `.IsUnique()` appear on the same entity.

**Why it matters:** the failure only appears after a delete, on the *next* sync, and a fail-closed wrapper converts it from a loud crash into permanent silent breakage — the worst combination. No happy-path test catches it.

**When it applies:** any new `ArchivableBaseEntity` with a unique natural-key index, and any service that re-derives rows from an external source on a schedule.

## `npm run codegen` needs a running host — new endpoints cannot reach the generated client offline (2026-08-06)

**What happened:** Adding 7 endpoints, the React layer needed client methods, but `nswag.config.json` reads `https://localhost:7187/openapi/v1.json` — it regenerates from a **running** app, which needs the database. With the local DB unavailable, `beaconApi()` could not gain the new methods, and hand-editing `src/api/generated/beacon-api.ts` is wrong (it is generated output and would be clobbered).

**Rule:** When you add endpoints and cannot run the host, call them through `fetchJson<T>(path, init)` from `src/lib/api.ts` instead of `beaconApi()`. It routes through the same `beaconFetch` wrapper (CSRF priming, antiforgery-mismatch retry, credentials) that the generated client uses, and is already the established pattern in `routes/home/queries.ts` and the auth pages. Keep the hand-written strict result interfaces either way (2026-06-02 lesson). NEVER hand-add methods to `src/api/generated/beacon-api.ts`.

**When it applies:** any new `/beacon/api/*` endpoint consumed by the React app while the host cannot be started.

## MCP SDK list-tools filters hand you process-wide singleton Tool instances — clone, never mutate (2026-08-16)

**What happened:** A list-tools request filter applied admin description overrides by assigning `tool.Description = override`. The SDK (ModelContextProtocol 2.2) exposes each tool's `ProtocolTool` as a per-process singleton, so the first override permanently destroyed the compiled `[Description]` — clearing the admin field could never restore it. Unit tests passed because they built throwaway `Tool` objects; the compliance review only caught it by decompiling the SDK and tracing object identity.

**Rule:** In any MCP request filter (or handler) that rewrites protocol objects, clone the object and replace the list element — never mutate what the SDK handed you. When testing such code, include a regression test that runs the rewrite twice against the SAME instances (set → cleared) and asserts the original is untouched.

**Why it matters:** Server-wide mutable state written from a request path is invisible in per-call tests and surfaces as "settings changes don't apply until restart" bug reports.

**When it applies:** Beacon.MCP filters/handlers touching `Tool`, `Resource`, `Prompt`, or any SDK protocol type; any cached/singleton object rewritten per request.

## Per-batch green does not mean tier-level correct — cross-batch conflicts need a whole-diff review (2026-08-16)

**What happened:** Five sequential implementer batches each finished with a green build and green targeted tests. The whole-diff compliance review then found: one batch's filter re-introduced exactly the cross-call state another batch existed to delete, and the stateless-resolution batch silently nulled the project attribution a previous tier's audit write relied on.

**Rule:** After multi-batch (or multi-subagent) implementation, always run at least one review pass over the ENTIRE diff against the spec's invariants — batch-local verification cannot see conflicts between batches.

**Why it matters:** Each subagent optimizes its own acceptance criteria; invariants that span batches ("no cross-call state anywhere", "every audit row carries a project") belong to no single batch.

**When it applies:** Any /mtk implement run on the subagent path; any tier/PR assembled from multiple independent work units.

## Beacon.AI cannot reach Beacon.Core internals — extract a public helper instead of duplicating a security primitive (2026-09-04)

**What happened:** Two batches of the `ask` SQL-correctness run needed Core helpers from Beacon.AI: `ColumnValueSampler.ContainsPiiValue` (caught by the plan-gap reviewer before coding → extracted as public `PiiValueScreen`) and `SqlIdentifierGuard` (missed in planning; the implementer duplicated the whitelist regex locally, the architecture reviewer flagged it, and the fix pass made the guard public). `Beacon.Core` grants `InternalsVisibleTo` only to `Beacon.SampleProject`, `Beacon.Api`, `Beacon.Tests`.

**Rule:** When a plan has Beacon.AI (or MCP) call a Core type, check its accessibility during planning (`grep -n 'internal' <file>`). If it is internal, the plan must either make it public (preferred for small, dependency-free static helpers) or extract a public helper — never let an implementer copy a security-relevant whitelist/escape routine. Add the visibility change to the manifest up front.

**When it applies:** any cross-project call into Beacon.Core from Beacon.AI/Beacon.MCP; especially SQL-composition, PII, and validation helpers.

## New settings columns: migration `defaultValue` must equal the code default (2026-09-04)

**What happened:** `AddGoldenExemplarSettings` (2026-07) added `enable_golden_exemplars` with `defaultValue: false` while the entity default is `true`, so every pre-existing `mcp_settings` row silently had the feature OFF. The `AddAskCorrectnessGrounding` migration was written with matching defaults (`true/12/true/2/false`) and verified by grepping both providers' migration files.

**Rule:** For every new column on a settings/config entity, set the migration `defaultValue` to the C# initializer value and verify it on BOTH providers before the batch is accepted (grep `defaultValue` in the new migration files). EF does not read C# initializers as DB defaults. Consider a one-off follow-up to flip `enable_golden_exemplars` for existing rows.

**When it applies:** any `McpSettings`/app-settings entity change; any default-on feature flag persisted as a column.

## `validate-handoff.sh` must be run with the branch base, not `main` (2026-09-04)

**What happened:** The mtk drift script defaults its base ref to `main`; on a long-lived feature branch it reported dozens of "files touched but not in change_manifest" that were earlier commits on the branch. Passing the run's base commit (`abc75cf`) as the second argument reduced the report to the expected pre-existing dirty paths, the `tasks/todo.md` progress file, and the git-mv delete.

**Rule:** Call `validate-handoff.sh <sidecar.json> <base-sha-of-this-run>` and read "declared but not touched" for `delete` entries as expected (a rename shows only the new path). Keep an orchestrator-side `comm` of `git diff --name-only <base>` vs manifest as the authoritative check.

**When it applies:** every mtk Phase 3.5 drift check on a feature branch.

## Optional enrichment arms must fail closed INCLUDING the voting arm (2026-09-04)

**What happened:** Self-consistency voting requested N-1 extra candidates concurrently via `Task.WhenAll`; a non-`AiServiceException` from one provider call (rate limit, network) propagated and would have failed the whole `ask` even though the already-validated single candidate existed. The silent-failure hunter caught it after implementation; the fix pass wrapped the vote in the standard fail-closed pattern and guarded each candidate individually. Related: the per-probe timeout catch in `ValueGroundingService` returned null without logging.

**Rule:** The 2026-07-13 fail-closed rule applies to *every* optional arm, not only retrieval blocks: voting, lint-repair, value probes, DISTINCT sampling. Each catch must (a) rethrow `OperationCanceledException`, (b) log a warning with identifiers only, (c) return the baseline. A `catch` with no log is a finding even when it degrades correctly.

**When it applies:** any `try/catch` added to `AskSqlPipeline`, `KnowledgeGraphService`, `ValueGroundingService`, `ColumnValueSampler`.

## Subagent implementers can be killed mid-batch by API spend limits — treat as NO_RESPONSE and respawn narrowed (2026-09-04)

**What happened:** The first B1 implementer (Opus) was terminated by an org spend-limit 429 after creating 5 files but before wiring DI/tests. The working tree was consistent (Beacon.AI built), so the respawn (Sonnet) received an "ALREADY DONE / REMAINING" note and finished in 12 minutes. Sonnet completed all remaining batches; Opus was not needed.

**Rule:** On an implementer failure notification, first `git status` + build the touched project to assess the partial state, then respawn ONCE with the same bundle plus an explicit done/remaining list; only escalate to inline-MAX if the second dispatch also fails. Default to Sonnet for implementers in this repo — the batch bundles are specific enough that Opus buys little.

**When it applies:** every mtk subagent-path run.

## SqlParserCS 0.6.5 never calls `Visitor.PreVisitQuery` — verify a visitor hook fires before building on it (2026-09-09)

**What happened:** `SqlSchemaValidator` registered CTE names in a `PreVisitQuery` override that had never executed since it shipped: the SqlParserCS `Visitor` base invokes `PreVisitStatement`, `PreVisitTableFactor` and `PreVisitExpression`, but not `PreVisitQuery`. CTE names were therefore never opaque, and the new projection-alias collection placed on the same hook was dead too. The gap surfaced only when the gate refactor's tests asserted `TablesUsed` and found the CTE name in the list.

**Rule:** Before relying on an AST-visitor override, prove the hook fires with a three-line trace visitor against the exact package version (`ParseSql(...).Visit(new TraceVisitor())`). Query-level scope (CTE names, projection aliases) is registered by an explicit pre-pass (`RegisterScopes`) and by the hooks that do fire (`PreVisitTableFactor` for derived tables, `PreVisitExpression` for `Subquery`/`InSubquery`/`Exists`). The same trace showed the visitor also does **not** descend into `TableFactor.Derived.SubQuery` — a derived table's inner tables and columns were invisible to the schema validator until `derived.SubQuery.Visit(this)` was added — while it does descend into top-level `WITH` bodies and `IN`/`EXISTS` subqueries.

**Why it matters:** A dead override compiles, reads as correct, and silently degrades a security-adjacent validator to a weaker mode. Reflection over properties (which the spec did) does not catch which callbacks a base class actually dispatches.

**When it applies:** Any override of a SqlParserCS `Visitor` member, and any AST library upgrade — re-run the trace.

## Host load kills subagents: probe `uptime` before dispatching, and run dotnet in the background under load (2026-09-09)

**What happened:** Two consecutive implementer subagents on one batch were killed by the harness stall watchdog ("no progress for 600s"). The batch was not at fault: the Mac's load average was ~100 (a runaway system process), so a 20-second `dotnet build` took 5-15 minutes and the agent produced no output while it waited. The mtk killed-mid-batch recovery attributes kills to the tier or the batch and has no branch for an overloaded host, so the RESUME respawn died the same way. Orphaned MSBuild worker nodes from the dead agents then slowed every later build.

**Rule:** Before dispatching implementer subagents (or any long `dotnet` command), check `uptime` load against core count. Under heavy load: run builds/tests as background Bash with a long timeout, pass `--disable-build-servers`, and after any killed dispatch kill orphaned `MSBuild.dll` nodes (parent PID 1) plus the stale `VBCSCompiler`. After a second kill on one batch, switch to inline-MAX rather than respawning.

**Why it matters:** ~45 minutes and two Opus dispatches were spent on a batch that was 80% done after the first kill.

**When it applies:** Any mtk implement run using subagents; any session where a build that took 20 s at baseline takes minutes.

## Moving logic from an injectable interface to a static helper breaks tests that stubbed the interface (2026-09-09)

**What happened:** The row-limit rewrite moved from `IQueryGuardrailService.ApplyRowLimit` (stubbed to identity in several fixtures) into the static `SqlRowLimitRewriter` called by the gate. A provider mock that matched the failing SQL by exact string stopped matching once the real cap was appended, and the eval test failed with an unrelated-looking assertion.

**Rule:** When a behaviour leaves an injectable seam, grep the test project for `Setup(x => x.<OldMember>` and for exact-string argument matches on downstream mocks; convert them to prefix/predicate matches or inject the new helper. Record the change as a helper-only edit in the spec's SC.

**Why it matters:** The failure surfaces far from the cause and looks like a behaviour regression.

**When it applies:** Any refactor that replaces an interface member with a pure/static implementation.

## A read endpoint that returns RESOLVED values must not feed a write endpoint that persists every field (2026-09-09)

**What happened:** `GetSettingsAsync` started returning lock/ceiling-resolved MCP settings so consumers see the effective value. The same call backs the admin GET, the React page seeds its form from it and re-sends every field on save, and the update handler wrote them straight to the entity. One unrelated global save would have replaced a stored `MaxRowLimit=9000` with the ceiling `1000` (and pinned a locked value) permanently — lifting the ceiling later would restore nothing. Thirty-six unit tests and the plan-gap review missed it; the whole-diff compliance review caught it because the hazard lives across the read path, the UI round-trip and the write path.

**Rule:** When a read model is derived (defaults, locks, ceilings, computed fields), either (a) return the raw stored row plus the derivation metadata to the editor, or (b) make the writer treat "derived value echoed back unchanged" as not-an-edit (Beacon: `UpdateMcpSettingsHandler.KeepStoredWhenClamped`, and locked fields keep the stored value). Add a test that stores a value above the ceiling, echoes the ceiling back, and asserts the stored value survived.

**Why it matters:** Silent, irreversible loss of admin configuration with no error and a green test suite.

**When it applies:** Any settings/profile/config screen whose GET applies policy (feature flags, tenant limits, RBAC-filtered fields) and whose PUT is a full-row replace.

## Inventory fixture breakage from constructor call sites, not from mock call sites (2026-09-09)

**What happened:** The spec listed every test fixture to migrate by grepping `new Mock<IMcpSettingsProvider>()`. `QueryExecutionService` gained an `IProjectContext` constructor parameter in the same batch; its one fixture did not build a settings mock and was missed. One compile error, found only at the batch checkpoint.

**Rule:** When a batch changes a constructor signature, the fixture inventory is `grep -rn "new <TypeName>(" src/Beacon.Tests` — one grep per changed constructor — in addition to any grep over mocked dependencies.

**Why it matters:** The manifest and the "fixtures to touch" list are sealed at approval; a fixture missing from them is a scope-guard event at implementation time.

**When it applies:** Any change that adds/removes/reorders a DI constructor parameter on a class instantiated directly in tests.

## A success-criterion observable must not ride on another test's failure message (2026-09-09)

**What happened:** SC7 ("both new handlers are exposed via HTTP") was to be verified by reading the missing-handler list in `OpenApiContractTests`' inherited failure message. On this host the inherited failure is a 404 fetching `/openapi/v1.json` from the harness, so the list is never produced and the criterion had no evidence channel. It was backed statically (grep of the endpoint map) instead.

**Rule:** Give every SC an evidence channel that works when the suite is green AND when it is red for unrelated reasons: a dedicated test, a deterministic grep/script, or a build artifact. Never "the failure message of test X will list…".

**Why it matters:** An SC with no observable is a criterion nobody can verify; the sidecar looked complete while one criterion was unverifiable by design.

**When it applies:** Writing `success_criteria[].verification` / `observable` in a spec sidecar.

## mtk `format-on-edit` runs Prettier defaults on TS/TSX when the repo has no Prettier config — declare the style first (2026-09-09)

**What happened:** The mtk PostToolUse/Stop hook `format-on-edit.sh` runs `npx --no-install prettier --write` on every `.ts/.tsx` written through Write/Edit. This repo has no `.prettierrc` and uses single quotes and `x =>` arrows, so two files came back in Prettier defaults (double quotes, `(x) =>`, width 80). Files edited via python/Bash were untouched, which made the churn look random; two subsequent exact-string edits failed on anchors that no longer existed. The collateral-guard does not flag quote-style rewrites (they are not whitespace-only).

**Rule:** Before the first TS/TSX edit in a repo without a Prettier config, either add the repo's style as a `.prettierrc` (single change, declared in the manifest) or set `MTK_FORMAT_ON_EDIT=0` for the session. If a file was already reformatted, normalize with an explicit invocation (`npx prettier --single-quote --arrow-parens avoid --print-width 100 --write`) and disclose the reflow of pre-existing blocks in the behavioral diff. Toolkit follow-up: the hook should skip Prettier when no config is found up-tree.

**Why it matters:** Style churn on hundreds of lines hides the real diff from reviewers and breaks anchor-based edits.

**When it applies:** Any mtk session that writes `.ts/.tsx/.js` files in a repo without a Prettier/Biome config.

## A shared test double must vary on the dimension the feature adds, or it hides the feature's own regressions (2026-09-09)

**What happened:** `SettingsProviderMock` was introduced so 21 fixtures kept compiling when consumers moved from `GetSettingsAsync()` to `GetEffectiveSettingsAsync(projectId)`. It delegated the effective call to the global stub and discarded `projectId`. Every fixture stayed green — and would have stayed green if a consumer resolved project 0 or the wrong project, silently downgrading a project's stricter PII / row-limit / read-only settings to the global ones. Two review lanes found it independently; the spec's "helpers only" rule had encouraged exactly this shape.

**Rule:** When a change adds a discriminator (project id, tenant, user, dialect) to a call, the test double for that call must be able to return a DIFFERENT value per discriminator (`SettingsProviderMock.Create(projectSettings: {[id] = …})`), and at least one consumer test per switched call site must (a) supply a differing value for the real id and assert the consumer's behaviour follows it, and (b) `Verify` the exact id reached the double and no other id did. "Compiles and stays green" is not the bar for a fixture migration.

**Why it matters:** The discriminator IS the feature; a double that ignores it turns the whole suite into a compile check for that feature.

**When it applies:** Any fixture helper introduced to absorb an interface widening; any `It.IsAny<int>()` on a newly added id parameter.

## A belt-and-braces design needs one test that composes both layers on the same entity (2026-09-11)

**What happened:** The content lock was enforced twice: at each write site (the brace) and by an EF `SaveChangesInterceptor` over a deny-list (the belt). The audit brace rewrites `McpAuditLog.Parameters` into a structural JSON shape instead of nulling it; the belt classified that column as Content and nulled it on the same save. The declared external contract therefore never reached the database. Both layers' own tests were green: the integration test used a capturing context whose `SaveChangesAsync` is a no-op, so the belt never ran, and the interceptor test asserted `Parameters == null` — pinning the bug as if it were the requirement. The tell was a public helper, `IsStructuralAuditParameters`, with zero production callers.

**Rule:** When two layers enforce the same rule over the same field, at least one test must exercise them **together on one entity**, and any layer that *transforms* rather than *clears* a value must publish a predicate the other layer consults (`McpRetentionRule.AlreadyRedacted`). Before accepting such a design, grep every helper the spec promises will be used: a helper with no production caller means the interaction was described but never wired.

**Why it matters:** Layer-local tests can both pass while the composition is wrong, and the failure is invisible until someone reads the database.

**When it applies:** Any brace+belt / validator+interceptor / middleware+handler pair; any spec sentence of the form "X leaves alone what Y already wrote".

## Gate the value, then pass the gated value — not the request (2026-09-11)

**What happened:** `RecordQueryFeedbackHandler` correctly nulled `signal.FeedbackNote` when a project forbids explicit feedback content, then two lines later sent `PromoteSignalToGoldenCommand(request.SignalId, request.Note)` — the raw, ungated request value — which the promotion copies into `McpEvalCase.Notes`. No race was needed: `RetainQueryContent=true` + `AllowExplicitFeedbackContent=false` is a documented configuration, and the interceptor belt could not catch it because the belt only fires when the content lock itself is on. The test that should have caught it asserted only `Verify(..., Times.Once)` on the promotion, never inspecting the command's payload.

**Rule:** After applying a policy gate to a field, every downstream copy of that field must read the **gated variable**, never the original request. When reviewing, grep the request object's field name after the gate line — any later use is a bypass. A `Verify(Times.Once)` on a command without an `It.Is<T>(...)` payload predicate proves the call happened, not that it carried the right data.

**Why it matters:** A privacy control that is enforced on the primary row and skipped on a derived copy is not enforced.

**When it applies:** Any handler that gates content and then dispatches a command/event carrying the same content; any redaction, masking, consent or retention rule with more than one persistence path.

## PostgreSQL reports a hit statement timeout as a cancellation (2026-09-11)

**What happened:** The error classifier mapped free-text provider errors onto a fixed vocabulary and checked `cancelled` before `timeout`. PostgreSQL's canonical statement-timeout message is `canceling statement due to statement timeout`, so every timeout was recorded as a user abort. A real user cancel is `canceling statement due to user request`. Found only when a test enumerated the whole vocabulary rather than the two classes already exercised.

**Rule:** In a keyword classifier over provider messages, order the table by specificity and pin the ambiguous messages with tests, because real messages routinely match several buckets. For PostgreSQL specifically: check `timeout` before `cancel`. When a class vocabulary is a declared contract, test every class plus the fallback, not the two that happen to appear elsewhere.

**Why it matters:** Under a content lock the class replaces the message, so a misclassification is the only thing the operator ever sees — and Wave 1.3 makes statement timeouts a first-class feature.

**When it applies:** Any error-classification table; any place a free-text diagnostic is reduced to an enum for retention or metrics.

## Adding an optional parameter before a trailing CancellationToken is never a one-file change (2026-09-11)

**What happened:** `LogToolCallAsync` gained an optional `tables` parameter before `CancellationToken ct = default`. Every existing caller passed the token positionally, so five MCP tool files had to switch to a named `ct:` argument. The spec's change manifest listed one file; the batch touched six.

**Rule:** When planning a signature change that inserts a parameter ahead of a trailing optional one, inventory the call sites first (`grep -rn "MethodName("`) and put them in the manifest. The compiler catches this one loudly, so it is scope drift rather than a silent bug — but an unplanned six-file batch is what the manifest exists to prevent.

**Why it matters:** Drift found at the batch checkpoint costs a sidecar amendment; drift found by a reviewer costs an iteration.

**When it applies:** Any C# signature change in a codebase that passes `CancellationToken` positionally.

## A subagent's verify step must not be a single multi-minute command (2026-09-11)

**What happened:** An implementer batch was killed six times. The runtime retried it five times on its own, each attempt dying at the 180-second no-progress watchdog. Host load was fine (0.3 per core). The batch's implementation was complete and compiling on disk the whole time: the stalls happened in its VERIFY step, a `dotnet test` filter spanning eleven fixtures that emits nothing for minutes. The orchestrator only sees the result after all six attempts have burned, so the skill's "a second kill halts the loop" rule never gets a chance to fire.

**Rule:** Give a dispatched implementer a verify step that produces output regularly — split a wide test filter into per-fixture runs, or have it run the suite in the background and poll. Watchdogs measure output, not progress. When a batch does come back killed, inventory first (`git status`, build): the partial work is often complete, and finishing the verify inline is far cheaper than a respawn.

**Why it matters:** Six wasted dispatches and roughly an hour, for a batch that was already done.

**When it applies:** Any mtk implement run on the subagent or dynamic-workflow path whose batch verification is one long command.

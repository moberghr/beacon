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

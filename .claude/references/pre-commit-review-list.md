# Pre-Commit Review List — Beacon

> Fast checklist for `/mtk review before commit`. Stack: dotnet (EF Core + MediatR). Max 10 items, ranked by likelihood of violation in this repo.

1. **`AsNoTracking()` on read queries.** Every read-only EF query that doesn't feed a write must add `AsNoTracking()`. Tracking on read paths is a perf trap.

2. **`.Select(new ...)` instead of `.Include()` for DTO reads.** Projection auto-joins; `.Include()` next to `.Select(...)` is dead code. Flag any new handler that mixes them.

3. **`CancellationToken` propagated through every async call.** `ToListAsync(ct)`, `FirstOrDefaultAsync(ct)`, `SaveChangesAsync(ct)`, downstream HTTP / LLM calls — all of them.

4. **One `SaveChangesAsync()` per handler.** Services called by handlers must NOT call `SaveChanges`. Multiple `SaveChanges` in a single handler is a smell — flag for review.

5. **NEVER edit a committed migration file.** Schema changes get a NEW migration in BOTH `Beacon.Core.PostgreSql` AND `Beacon.Core.SqlServer`. Diffing an existing migration file is a hard stop.

6. **No plaintext secrets, no missing encryption.** No connection strings, API keys, encryption keys, OIDC client secrets, or LLM provider keys in code, tests, or appsettings (non-encrypted sections). Connection strings persisted to DB MUST go through the encryption helper using `Beacon:EncryptionKey`.

7. **No PII in logs — and mask PII in returned rows.** User query text, full row payloads, connection strings, auth tokens — none go to `ILogger` (identifiers and counts only). Separately: any MCP/AI surface returning provider rows to a client must run the detected `PiiColumns` through `MaskPiiValues` before formatting — recomputed from the SQL that actually executes (after any dry-run/repair), matching `SemanticSearchService`. Computing `PiiColumns` and discarding it is a leak. **Every MCP SQL execution path validates through `ISqlExecutionGate`** (`Beacon.Core.Services.Validation`): a direct `SqlReadOnlyAstValidator.Validate`, `SqlSchemaValidator.Validate` or `ApplyRowLimit` call in `Beacon.MCP` or `Beacon.AI/Services/{Mcp,Eval}` is a flag, and any repaired/retried SQL must be re-evaluated by the gate before it executes.

8. **No fake / seed / demo data in UI pages.** Pages start empty; data comes from real sources. Hardcoded sample rows in `.razor` are an automatic flag.

9. **Tests added for new public methods / handlers.** Non-trivial LINQ → translation test in `QueryTranslationTests.cs`. Pure logic → NUnit unit test. React UI → Vitest + RTL. NEVER `UseInMemoryDatabase`.

10. **Build + format clean.** `dotnet build --property WarningLevel=0` passes, `dotnet format --verify-no-changes` passes, no new warnings introduced.

11. **A derived READ must not feed a full-row WRITE.** If a GET applies policy (locks, ceilings, defaults, RBAC-filtered fields) and the matching PUT replaces every field, the writer must either receive the raw stored values + derivation metadata or treat "derived value echoed back unchanged" as not-an-edit (`UpdateMcpSettingsHandler.KeepStoredWhenClamped`, `McpLockPolicy.KeepStored`). One unrelated save otherwise bakes the policy value into the row. Flag any settings/config handler pair where the GET calls a `*Effective*`/resolved accessor.

12. **A shared test double must vary on a newly added discriminator.** When a call gains a project/tenant/user/dialect parameter, `It.IsAny<int>()` on that parameter in a shared mock helper hides a wrong-id regression across every fixture. The helper must accept a per-id map (`SettingsProviderMock.Create(projectSettings:)`) and at least one consumer test per switched call site must assert a differing value follows the id and `Verify` that no other id was requested.

13. **Two layers enforcing one rule need a test that composes them.** Brace + belt, validator + interceptor, middleware + handler: each layer's own test can pass while the composition is wrong. If one layer *transforms* a value rather than clearing it, the other must consult a shared predicate (`McpRetentionRule.AlreadyRedacted`). A helper the spec promises will be used, with zero production callers, means the interaction was described but never wired — grep for callers before believing it.

14. **After gating a field, pass the gated variable — never the original request.** Grep the request object's field name after the gate line; any later use is a bypass (a golden-case promotion re-read `request.Note` after the handler had nulled `signal.FeedbackNote`). A `Verify(..., Times.Once)` without an `It.Is<T>(...)` payload predicate proves a call happened, not that it carried the right data.

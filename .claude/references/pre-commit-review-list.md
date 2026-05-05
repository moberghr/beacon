# Pre-Commit Review List — Beacon

> Fast checklist for `/mtk review before commit`. Stack: dotnet (EF Core + MediatR). Max 10 items, ranked by likelihood of violation in this repo.

1. **`AsNoTracking()` on read queries.** Every read-only EF query that doesn't feed a write must add `AsNoTracking()`. Tracking on read paths is a perf trap.

2. **`.Select(new ...)` instead of `.Include()` for DTO reads.** Projection auto-joins; `.Include()` next to `.Select(...)` is dead code. Flag any new handler that mixes them.

3. **`CancellationToken` propagated through every async call.** `ToListAsync(ct)`, `FirstOrDefaultAsync(ct)`, `SaveChangesAsync(ct)`, downstream HTTP / LLM calls — all of them.

4. **One `SaveChangesAsync()` per handler.** Services called by handlers must NOT call `SaveChanges`. Multiple `SaveChanges` in a single handler is a smell — flag for review.

5. **NEVER edit a committed migration file.** Schema changes get a NEW migration in BOTH `Beacon.Core.PostgreSql` AND `Beacon.Core.SqlServer`. Diffing an existing migration file is a hard stop.

6. **No plaintext secrets, no missing encryption.** No connection strings, API keys, encryption keys, OIDC client secrets, or LLM provider keys in code, tests, or appsettings (non-encrypted sections). Connection strings persisted to DB MUST go through the encryption helper using `Beacon:EncryptionKey`.

7. **No PII in logs.** User query text, full row payloads, connection strings, auth tokens — none of them go to `ILogger`. Identifiers and counts only.

8. **No fake / seed / demo data in UI pages.** Pages start empty; data comes from real sources. Hardcoded sample rows in `.razor` are an automatic flag.

9. **Tests added for new public methods / handlers.** Non-trivial LINQ → translation test in `QueryTranslationTests.cs`. Pure logic → NUnit unit test. UI → bUnit. NEVER `UseInMemoryDatabase`.

10. **Build + format clean.** `dotnet build --property WarningLevel=0` passes, `dotnet format --verify-no-changes` passes, no new warnings introduced.

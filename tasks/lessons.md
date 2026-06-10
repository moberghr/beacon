# Lessons Learned

## NSwag generates intentionally loose types — local strict result interfaces are deliberate (2026-06-02)

**What happened:** A "refactor" to delete duplicated result interfaces and import generated types (to drop ~92 `as unknown as` casts) was premised on the casts being gratuitous. They are not.

**Rule:** Do NOT replace hand-written result/command interfaces in `Beacon.UI/web/src/routes/**/queries.ts` with imports from `src/api/generated/beacon-api.ts`. The NSwag config (`nswag.config.json`) sets `markOptionalProperties: true`, emits a `[key: string]: any` index signature on all 223 interfaces, and types `DateTime` as `Date` even though the client deserializes with a plain `JSON.parse` (no reviver) so dates are strings at runtime. The local interfaces are stricter and more correct.

**Instead:** bridge the loose generated payload into the strict local type at the call boundary via `unwrap<T>()` in `src/lib/api.ts` (the single, greppable trust boundary; add zod here later if needed). The "92 casts" were 5 unrelated categories — only the ~40 named-result-type double-casts are the addressable ones; `as never` command args, react-hook-form `register('x' as never)`, Monaco, and Date-field casts are legitimate and unrelated.

**Why it matters:** Importing the generated types would explode `tsc` errors (optional everywhere, Date vs string) and *reduce* type quality. Verify codegen output shape before assuming duplication is debt.

**When it applies:** Any time generated-client types look "duplicated" by local interfaces in this repo.

## Beacon.Tests has no global usings — new test files need `using NUnit.Framework;` (2026-06-10)

**What happened:** Four new test files failed to compile with `TestFixtureAttribute could not be found`.

**Rule:** Every new test file in Beacon.Tests must explicitly include `using NUnit.Framework;` — the project does not define global usings for the test framework.

**Why it matters:** The error surfaces only at `dotnet test` (the main solution build skips the test project), so it's easy to claim a green build prematurely.

**When it applies:** Any new file under Beacon.Tests/.

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

**What happened:** `Beacon.Tests/Integration/Api/Phase1HarnessTests.cs` fails with "No authenticationScheme was specified, and there was no DefaultChallengeScheme found" — verified failing on clean HEAD via a throwaway worktree.

**Rule:** Treat this single failure as the known baseline until the harness registers a default challenge scheme; do not block unrelated merges on it, and do not silently include a fix in unrelated work.

**When it applies:** Interpreting `dotnet test` results on this repo (expect N-1 passes until fixed).

# Testing

> §4.x — Frameworks, naming, query translation. Loaded automatically by Claude Code.

## Stack

§4.1 **Framework set:** NUnit 4 + Moq + FluentAssertions. All tests live in `src/Beacon.Tests/`. Do NOT introduce xUnit, MSTest, NSubstitute, or FakeItEasy.

§4.2 **Test project target:** `net10.0`, matches the rest of the solution.

## Query translation tests (primary strategy)

§4.3 **EF Core LINQ → PostgreSQL SQL translation is verified via `ToQueryString()`.** No real database needed. See `src/Beacon.Tests/Integration/QueryTranslationTests.cs`.

§4.4 **Translation tests use `NpgsqlTestContext.Create()`** — creates a context with the Npgsql provider and snake_case naming, against a dummy connection string (no DB hit).

§4.5 **Naming for translation tests:** `{QueryOrFeatureName}_Translates()`.

§4.6 **When you add a non-trivial handler, add a corresponding translation test.** Required for any handler whose LINQ touches `JOIN`, `GROUP BY`, raw fragments, JSON columns, arrays, or filters that can break Npgsql translation.

## Forbidden test infrastructure

§4.7 **NEVER use `UseInMemoryDatabase`** — it doesn't catch provider-specific translation issues, which is the whole reason these tests exist. The `Microsoft.EntityFrameworkCore.InMemory` package is not referenced and must not be re-added. To exercise code that calls async EF operators through a context without a real DB, back a mocked `DbSet<T>` with the async-queryable doubles in `src/Beacon.Tests/Common/TestAsyncQueryable.cs` (see `QueryServiceReadOnlyGateTests`).

## Other tests

§4.8 **Unit tests:** pure logic, validators, mapping, branching rules — NUnit + Moq + FluentAssertions.

§4.9 _(removed — no Razor/Blazor UI remains after the Phase 3 cutover, so bUnit is no longer part of the stack. The UI is the React app; frontend tests use Vitest + RTL + MSW under `src/Beacon.UI/web/src`.)_

§4.10 **Assertions must be meaningful.** `result.Should().NotBeNull()` alone is not a test — assert structure or values that prove the behavior under test.

## Project authorization tests

§4.11 **Project-authorization coverage is test-enforced — extend it, never bypass it.** `ProjectAuthorizationCoverageTests` fails when a request's `*Id`/`*Ids` property is neither in a `ProjectCheck` nor exempt with a reason; a new by-id route must deny in `ByIdRouteDenialTests` (owner- or admin-only routes go on the explicit allow-list). Every `IProjectFilteredRequest` needs a scenario in `ProjectListScopeShapeTests` whose SQL carries the scope predicate under a restricted scope. `ProjectCheckManifestTests` pins each by-id request's resource kind, id property and action: a changed check updates the manifest on purpose, never silently.

§4.12 **Fast-path SQL goldens.** On the fast path (nothing restricted) each list handler must send exactly the SQL in `Unit/ProjectAuthorization/FastPathGolden/*.sql`. When a list query changes on purpose, re-record with `BEACON_RECORD_FASTPATH_GOLDEN=<golden dir>` and check the SQL diff. Translation tests that must hold on both providers use `NpgsqlTestContext` and `SqlServerTestContext`.

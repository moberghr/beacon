# Testing

> §4.x — Frameworks, naming, query translation. Loaded automatically by Claude Code.

## Stack

§4.1 **Framework set:** NUnit 4 + Moq + FluentAssertions. All tests live in `src/Beacon.Tests/`. Do NOT introduce xUnit, MSTest, NSubstitute, or FakeItEasy.

§4.2 **Test project target:** `net9.0`, matches the rest of the solution.

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

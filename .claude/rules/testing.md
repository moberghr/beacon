# Testing

> §4.x — Frameworks, naming, query translation. Loaded automatically by Claude Code.

## Stack

§4.1 **Framework set:** NUnit 4 + Moq + FluentAssertions + bUnit. All tests live in `Beacon.Tests/`. Do NOT introduce xUnit, MSTest, NSubstitute, or FakeItEasy.

§4.2 **Test project target:** `net9.0`, matches the rest of the solution.

## Query translation tests (primary strategy)

§4.3 **EF Core LINQ → PostgreSQL SQL translation is verified via `ToQueryString()`.** No real database needed. See `Beacon.Tests/Integration/QueryTranslationTests.cs`.

§4.4 **Translation tests use `NpgsqlTestContext.Create()`** — creates a context with the Npgsql provider and snake_case naming, against a dummy connection string (no DB hit).

§4.5 **Naming for translation tests:** `{QueryOrFeatureName}_Translates()`.

§4.6 **When you add a non-trivial handler, add a corresponding translation test.** Required for any handler whose LINQ touches `JOIN`, `GROUP BY`, raw fragments, JSON columns, arrays, or filters that can break Npgsql translation.

## Forbidden test infrastructure

§4.7 **NEVER use `UseInMemoryDatabase`** — it doesn't catch provider-specific translation issues, which is the whole reason these tests exist.

⚠️ **Inconsistency:** `Beacon.Tests/Beacon.Tests.csproj` still references `Microsoft.EntityFrameworkCore.InMemory` (9.0.4). The package is dead weight today — do NOT use it in new tests; remove the reference next time `Beacon.Tests.csproj` is touched for an unrelated reason.

## Other tests

§4.8 **Unit tests:** pure logic, validators, mapping, branching rules — NUnit + Moq + FluentAssertions.

§4.9 **bUnit for Razor components.** Test rendered HTML and event interactions, not Blazor runtime internals.

§4.10 **Assertions must be meaningful.** `result.Should().NotBeNull()` alone is not a test — assert structure or values that prove the behavior under test.

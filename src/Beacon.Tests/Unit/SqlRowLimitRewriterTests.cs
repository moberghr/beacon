using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Services.Validation;

namespace Beacon.Tests.Unit;

[TestFixture]
public class SqlRowLimitRewriterTests
{
    [Test]
    public void Apply_Postgres_SubqueryLimit_StillCapsOuter()
    {
        // A LIMIT in a subquery bounds an intermediate result — the outer result is still unbounded.
        var result = SqlRowLimitRewriter.Apply("SELECT * FROM t WHERE id IN (SELECT id FROM u LIMIT 5)", 100, "PostgreSQL");

        result.Sql.Should().EndWith(" LIMIT 100");
        result.Outcome.Should().Be(SqlRowLimitOutcome.Applied);
    }

    [Test]
    public void Apply_Postgres_LimitInsideStringLiteral_StillCapsOuter()
    {
        var result = SqlRowLimitRewriter.Apply("SELECT * FROM t WHERE note = 'LIMIT 5'", 100, "PostgreSQL");

        result.Sql.Should().EndWith(" LIMIT 100");
        result.Outcome.Should().Be(SqlRowLimitOutcome.Applied);
    }

    [Test]
    public void Apply_TSql_SubqueryTop_StillCapsOuter()
    {
        var result = SqlRowLimitRewriter.Apply("SELECT a FROM (SELECT TOP 5 a FROM t) x", 100, "MSSQL");

        result.Sql.Should().StartWith("SELECT TOP 100 a FROM (SELECT TOP 5");
        result.Outcome.Should().Be(SqlRowLimitOutcome.Applied);
    }

    [Test]
    public void Apply_TSql_Distinct_PutsTopAfterDistinct()
    {
        // T-SQL requires `SELECT DISTINCT TOP n`; `TOP n DISTINCT` is a syntax error.
        var result = SqlRowLimitRewriter.Apply("SELECT DISTINCT a FROM t", 100, "MSSQL");

        result.Sql.Should().Be("SELECT DISTINCT TOP 100 a FROM t");
        result.Outcome.Should().Be(SqlRowLimitOutcome.Applied);
    }

    [Test]
    public void Apply_TSql_Union_UsesOrderByOffsetFetch()
    {
        // A leading TOP would cap the first UNION arm and leave the combined result unbounded.
        var result = SqlRowLimitRewriter.Apply("SELECT a FROM t UNION SELECT b FROM u", 100, "MSSQL");

        result.Sql.Should().EndWith("ORDER BY (SELECT NULL) OFFSET 0 ROWS FETCH NEXT 100 ROWS ONLY");
        result.Sql.Should().NotContain("TOP");
        result.Outcome.Should().Be(SqlRowLimitOutcome.Applied);
    }

    [Test]
    public void Apply_TrailingLineComment_AppendsOnNewLine()
    {
        var result = SqlRowLimitRewriter.Apply("SELECT a FROM t -- all rows", 100, "PostgreSQL");

        result.Sql.Should().Be("SELECT a FROM t -- all rows\nLIMIT 100");
        result.Outcome.Should().Be(SqlRowLimitOutcome.Applied);
    }

    [Test]
    public void Apply_OuterAlreadyLimited_Unchanged()
    {
        var limit = SqlRowLimitRewriter.Apply("SELECT a FROM t LIMIT 10", 100, "PostgreSQL");

        limit.Sql.Should().Be("SELECT a FROM t LIMIT 10");
        limit.Outcome.Should().Be(SqlRowLimitOutcome.AlreadyLimited);

        var top = SqlRowLimitRewriter.Apply("SELECT TOP 3 a FROM t", 100, "MSSQL");

        top.Sql.Should().Be("SELECT TOP 3 a FROM t");
        top.Outcome.Should().Be(SqlRowLimitOutcome.AlreadyLimited);

        var fetch = SqlRowLimitRewriter.Apply("SELECT a FROM t ORDER BY a OFFSET 0 ROWS FETCH NEXT 7 ROWS ONLY", 100, "MSSQL");

        fetch.Sql.Should().Be("SELECT a FROM t ORDER BY a OFFSET 0 ROWS FETCH NEXT 7 ROWS ONLY");
        fetch.Outcome.Should().Be(SqlRowLimitOutcome.AlreadyLimited);
    }

    [Test]
    public void Apply_TSql_OrderBy_UsesOffsetFetch()
    {
        var result = SqlRowLimitRewriter.Apply("SELECT a FROM t ORDER BY a", 100, "MSSQL");

        result.Sql.Should().EndWith("OFFSET 0 ROWS FETCH NEXT 100 ROWS ONLY");
        result.Sql.Should().NotContain("TOP");
        result.Outcome.Should().Be(SqlRowLimitOutcome.Applied);
    }

    [Test]
    public void Apply_TSql_CteLeading_UsesOrderByOffsetFetch()
    {
        // A leading TOP would land on the CTE body and leave the outer result uncapped.
        var result = SqlRowLimitRewriter.Apply("WITH x AS (SELECT a FROM t) SELECT a FROM x", 100, "MSSQL");

        result.Sql.Should().Contain("FETCH NEXT 100 ROWS ONLY");
        result.Sql.Should().NotContain("TOP");
        result.Outcome.Should().Be(SqlRowLimitOutcome.Applied);
    }

    [Test]
    public void Apply_ExplainTable_Unchanged()
    {
        var result = SqlRowLimitRewriter.Apply("DESCRIBE t", 100, "MySQL");

        result.Sql.Should().Be("DESCRIBE t");
        result.Outcome.Should().Be(SqlRowLimitOutcome.NotApplicable);
    }

    [Test]
    public void Apply_MaxRowsZero_Unchanged()
    {
        var result = SqlRowLimitRewriter.Apply("SELECT a FROM t", 0, "PostgreSQL");

        result.Sql.Should().Be("SELECT a FROM t");
        result.Outcome.Should().Be(SqlRowLimitOutcome.NotApplicable);
    }

    [Test]
    public void Apply_Unparseable_FallsBackToTextual()
    {
        // Unparseable SQL must still be bounded — the read-only gate decides whether it may run at all.
        var result = SqlRowLimitRewriter.Apply("SELECT ??? FROM", 100, "PostgreSQL");

        result.Outcome.Should().Be(SqlRowLimitOutcome.TextualFallback);
        result.Sql.Should().EndWith(" LIMIT 100");
    }

    [Test]
    public void Apply_Explain_CapsInnerSelect()
    {
        var result = SqlRowLimitRewriter.Apply("EXPLAIN SELECT a FROM t", 100, "PostgreSQL");

        result.Sql.Should().EndWith(" LIMIT 100");
        result.Outcome.Should().Be(SqlRowLimitOutcome.Applied);
    }

    [Test]
    public void Apply_TSql_OffsetWithoutFetch_AppendsOnlyFetch()
    {
        // `ORDER BY a OFFSET 10 ROWS` is legal T-SQL with no bound; appending a second OFFSET would be a syntax error.
        var result = SqlRowLimitRewriter.Apply("SELECT a FROM t ORDER BY a OFFSET 10 ROWS", 100, "MSSQL");

        result.Sql.Should().Be("SELECT a FROM t ORDER BY a OFFSET 10 ROWS FETCH NEXT 100 ROWS ONLY");
        result.Outcome.Should().Be(SqlRowLimitOutcome.Applied);
    }

    [Test]
    public void Apply_Unparseable_ReportsParserExceptionType_NotSql()
    {
        var result = SqlRowLimitRewriter.Apply("SELECT ??? FROM", 100, "PostgreSQL");

        result.FallbackReason.Should().NotBeNullOrWhiteSpace();
        result.FallbackReason.Should().NotContain("???", "the fallback reason is an exception type, never SQL text (§1.11)");
    }
}

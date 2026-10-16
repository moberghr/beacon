using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Services.Security;
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

    [TestCase("MSSQL")]
    [TestCase("AzureSynapse")]
    public void Apply_TSql_NestedLeadingComment_PutsTopOnTheRealSelect(string dialect)
    {
        // T-SQL nests block comments: the first `*/` closes only the inner one, so the first SELECT is still
        // commented out. A TOP placed there would leave the query uncapped.
        var result = SqlRowLimitRewriter.Apply("/* /* */ SELECT */ SELECT a FROM t", 100, dialect);

        result.Outcome.Should().Be(SqlRowLimitOutcome.Applied);
        result.Sql.Should().Be("/* /* */ SELECT */ SELECT TOP 100 a FROM t");
    }

    [Test]
    public void Apply_TSql_LeadingCommentsAndDistinct_PutsTopAfterDistinct()
    {
        var result = SqlRowLimitRewriter.Apply("-- note\n/* a /* b */ c */ select distinct a FROM t", 100, "MSSQL");

        result.Sql.Should().Be("-- note\n/* a /* b */ c */ select distinct TOP 100 a FROM t");
        result.Outcome.Should().Be(SqlRowLimitOutcome.Applied);
    }

    // A leading TOP would cap the first arm only, and SQL Server refuses ORDER BY (SELECT NULL) after a set operation (an
    // ORDER BY item must be in its select list): the whole statement becomes a derived table capped by TOP.
    [TestCase("MSSQL", "SELECT a FROM t UNION SELECT b FROM u")]
    [TestCase("MSSQL", "SELECT a FROM t UNION ALL SELECT b FROM u")]
    [TestCase("MSSQL", "SELECT a FROM t EXCEPT SELECT b FROM u")]
    [TestCase("AzureSynapse", "SELECT a FROM t INTERSECT SELECT b FROM u")]
    [TestCase("MSSQL", "(SELECT a FROM t)")]
    public void Apply_TSql_SetOperation_IsWrappedInACappedDerivedTable(string dialect, string sql)
    {
        var result = SqlRowLimitRewriter.Apply(sql, 100, dialect);

        result.Outcome.Should().Be(SqlRowLimitOutcome.Applied);
        result.Sql.Should().Be($"SELECT TOP 100 * FROM (\n{sql}\n) AS beacon_rows");
        result.Sql.Should().NotContain("(SELECT NULL)");
        ShouldParseAsTSql(result.Sql);
    }

    [Test]
    public void Apply_TSql_SetOperationAfterACteList_WrapsOnlyTheBody()
    {
        // A CTE cannot sit inside a derived table, so the CTE list stays in front and the body is wrapped.
        const string ctes = "WITH x AS (SELECT a FROM t), y (b) AS (SELECT b FROM u) ";
        const string body = "SELECT a FROM x UNION ALL SELECT b FROM y";

        var result = SqlRowLimitRewriter.Apply(ctes + body, 100, "MSSQL");

        result.Outcome.Should().Be(SqlRowLimitOutcome.Applied);
        result.Sql.Should().Be($"{ctes}SELECT TOP 100 * FROM (\n{body}\n) AS beacon_rows");
        ShouldParseAsTSql(result.Sql);
    }

    [Test]
    public void Apply_TSql_OrderedSetOperation_KeepsItsOrderAndUsesOffsetFetch()
    {
        var result = SqlRowLimitRewriter.Apply("SELECT a FROM t UNION SELECT b FROM u ORDER BY a", 100, "MSSQL");

        result.Sql.Should().Be("SELECT a FROM t UNION SELECT b FROM u ORDER BY a OFFSET 0 ROWS FETCH NEXT 100 ROWS ONLY");
        result.Outcome.Should().Be(SqlRowLimitOutcome.Applied);
    }

    [Test]
    public void Apply_Postgres_SetOperation_AppendsLimitToTheWholeResult()
    {
        var result = SqlRowLimitRewriter.Apply("SELECT a FROM t UNION SELECT b FROM u", 100, "PostgreSQL");

        result.Sql.Should().Be("SELECT a FROM t UNION SELECT b FROM u LIMIT 100");
        result.Outcome.Should().Be(SqlRowLimitOutcome.Applied);
    }

    [Test]
    public void Apply_TSql_TheEnumDiscoveryStatement_IsCappedAsAWhole()
    {
        var sql = Beacon.AI.Services.Documentation.ProjectDocumentationService.BuildEnumDiscoveryQuery(
            Beacon.Core.Data.Enums.DatabaseEngineType.MSSQL, "dbo", "orders", ["status", "kind"]);

        var result = SqlRowLimitRewriter.Apply(sql, 100, "MSSQL");

        result.Outcome.Should().Be(SqlRowLimitOutcome.Applied);
        result.Sql.Should().StartWith("SELECT TOP 100 * FROM (\n").And.EndWith("\n) AS beacon_rows");
        ShouldParseAsTSql(result.Sql);
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

    // A cap of zero or below is refused, never read as "no cap", so the caller cannot run the statement uncapped.
    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(int.MinValue)]
    public void Apply_NonPositiveMaxRows_Refused(int maxRows)
    {
        var result = SqlRowLimitRewriter.Apply("SELECT a FROM t", maxRows, "PostgreSQL");

        result.Outcome.Should().Be(SqlRowLimitOutcome.Refused);
        result.RefusalCode.Should().Be(SqlGateCodes.InvalidRowCap);
        result.RefusalMessage.Should().Be(SqlRowLimitRewriter.InvalidRowCapMessage);
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void ApplyRowLimit_NonPositiveMaxRows_Throws(int maxRows)
    {
        var act = () => new QueryGuardrailService().ApplyRowLimit("SELECT a FROM t", maxRows, "PostgreSQL");

        act.Should().Throw<InvalidOperationException>().WithMessage(SqlRowLimitRewriter.InvalidRowCapMessage);
    }

    [Test]
    public void Apply_Unparseable_FallsBackToTextual()
    {
        // Unparseable SQL must still be bounded — the read-only gate decides whether it may run at all.
        var result = SqlRowLimitRewriter.Apply("SELECT ??? FROM", 100, "PostgreSQL");

        result.Outcome.Should().Be(SqlRowLimitOutcome.TextualFallback);
        result.Sql.Should().EndWith(" LIMIT 100");
    }

    // In the textual fallback only a bound outside literals, quoted names and comments counts; with ambiguous quoting
    // nothing counts and the cap is always added.
    [TestCase("SELECT 'LIMIT 1' AS x FROM t WHERE ???", "SELECT 'LIMIT 1' AS x FROM t WHERE ??? LIMIT 10")]
    [TestCase("SELECT a FROM t /* LIMIT 1 */ WHERE ???", "SELECT a FROM t /* LIMIT 1 */ WHERE ??? LIMIT 10")]
    [TestCase("SELECT a FROM t WHERE ??? -- LIMIT 1", "SELECT a FROM t WHERE ??? -- LIMIT 1\nLIMIT 10")]
    [TestCase("SELECT \"LIMIT 1\" FROM t WHERE ???", "SELECT \"LIMIT 1\" FROM t WHERE ??? LIMIT 10")]
    [TestCase("SELECT 'a\\' LIMIT 1 ' FROM t WHERE ???", "SELECT 'a\\' LIMIT 1 ' FROM t WHERE ??? LIMIT 10")]
    [TestCase("SELECT $$ LIMIT 1 $$ FROM t WHERE ???", "SELECT $$ LIMIT 1 $$ FROM t WHERE ??? LIMIT 10")]
    [TestCase("SELECT a FROM t WHERE ??? LIMIT 5", "SELECT a FROM t WHERE ??? LIMIT 5")]
    public void Apply_TextualFallback_TrustsOnlyABoundOutsideLiteralsAndComments(string sql, string expected)
    {
        var result = SqlRowLimitRewriter.Apply(sql, 10, "PostgreSQL");

        result.Outcome.Should().Be(SqlRowLimitOutcome.TextualFallback);
        result.Sql.Should().Be(expected);
    }

    [Test]
    public void Apply_TextualFallback_TSqlTopInsideABracketedName_StillCaps()
    {
        var result = SqlRowLimitRewriter.Apply("SELECT [TOP 5] FROM t WHERE ???", 10, "MSSQL");

        result.Outcome.Should().Be(SqlRowLimitOutcome.TextualFallback);
        result.Sql.Should().Be("SELECT TOP 10 [TOP 5] FROM t WHERE ???");
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

    private static void ShouldParseAsTSql(string sql)
    {
        var act = () => new SqlParser.Parser().ParseSql(sql, new SqlParser.Dialects.MsSqlDialect());

        act.Should().NotThrow($"`{sql}` must be valid T-SQL");
    }
}

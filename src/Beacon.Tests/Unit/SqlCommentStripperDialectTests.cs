using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// Comment removal before the regex guardrail, over every dialect the gate judges: text that only looks like a comment —
/// inside a string, a dollar-quoted string or a quoted identifier — is kept, a statement hidden between such text is still
/// refused, each engine's own comment markers are honoured (and only its own), and SQL the stripper cannot read safely is
/// judged as written. The gate passes the source's dialect to the stripper, so a commented statement runs on every engine.
/// </summary>
[TestFixture]
public class SqlCommentStripperDialectTests
{
    private static readonly string[] Dialects = ["PostgreSQL", "MySQL", "MSSQL", "AzureSynapse", "Snowflake", "BigQuery", "Databricks", "SQLite"];

    private static IEnumerable<TestCaseData> EveryDialect(params string[] statements)
    {
        foreach (var statement in statements)
        {
            foreach (var dialect in Dialects)
            {
                yield return new TestCaseData(statement, dialect);
            }
        }
    }

    private static IEnumerable<TestCaseData> StatementsHiddenBetweenCommentMarkers() => EveryDialect(
        "SELECT '/*' AS a; DELETE FROM t; SELECT '*/' AS b",
        "SELECT '--' AS a; DELETE FROM t; SELECT 1 AS b",
        "SELECT '/*' AS a; DELETE FROM t -- '*/'",
        "SELECT 1 AS a /* note */; DELETE FROM t",
        "-- note\nDELETE FROM t");

    private static IEnumerable<TestCaseData> StatementsExplainedInComments() => EveryDialect(
        "-- delete old rows first\nSELECT id FROM orders",
        "/* drop the old copy first */ SELECT id FROM orders",
        "SELECT id -- update weekly\r\n\tFROM orders");

    [TestCaseSource(nameof(StatementsHiddenBetweenCommentMarkers))]
    public void AWriteBetweenTextThatLooksLikeComments_IsRefused(string sql, string dialect)
    {
        var report = Gate(sql, dialect);

        report.Blocked.Should().BeTrue($"{dialect} must not run a second statement");
    }

    // Without the dialect the stripper keeps the comments and the guardrail refuses a statement that starts with one.
    [TestCaseSource(nameof(StatementsExplainedInComments))]
    public void TheGate_JudgesACommentedStatementInItsDialect_AndLetsItRun(string sql, string dialect)
    {
        var report = Gate(sql, dialect);

        report.Blocked.Should().BeFalse(report.BlockReason);
    }

    // `#` starts a comment only on MySQL and BigQuery; elsewhere the text after it is SQL and is judged.
    [TestCase("MySQL", false)]
    [TestCase("BigQuery", false)]
    [TestCase("PostgreSQL", true)]
    [TestCase("MSSQL", true)]
    [TestCase("Snowflake", true)]
    [TestCase("Databricks", true)]
    [TestCase("SQLite", true)]
    public void AHashStartsACommentOnlyWhereTheEngineReadsItAsOne(string dialect, bool blocked)
    {
        Gate("SELECT 1 AS a # DELETE FROM t", dialect).Blocked.Should().Be(blocked);
    }

    [TestCase("SELECT $$ /* not a comment */ $$ AS a -- note", "PostgreSQL", "SELECT $$ /* not a comment */ $$ AS a  ")]
    [TestCase("SELECT \"a--b\" FROM t /* note */", "PostgreSQL", "SELECT \"a--b\" FROM t  ")]
    [TestCase("SELECT \"a/*b\" FROM t -- note", "Snowflake", "SELECT \"a/*b\" FROM t  ")]
    [TestCase("SELECT [a/*b] FROM t -- note", "MSSQL", "SELECT [a/*b] FROM t  ")]
    [TestCase("SELECT `a#b` FROM t # note", "MySQL", "SELECT `a#b` FROM t  ")]
    [TestCase("SELECT `a--b` FROM t -- note", "Databricks", "SELECT `a--b` FROM t  ")]
    public void MarkersInsideQuotedTextAndIdentifiers_AreKept(string sql, string dialect, string expected)
    {
        SqlCommentStripper.Strip(sql, dialect).Should().Be(expected);
    }

    [TestCase("SELECT $$/*$$ AS a; DELETE FROM t; SELECT $$*/$$ AS b")]
    [TestCase("SELECT $tag$ -- $tag$ AS a; DELETE FROM t")]
    public void AWriteBetweenDollarQuotedCommentMarkers_IsRefused(string sql)
    {
        Gate(sql, "PostgreSQL").Blocked.Should().BeTrue();
    }

    // Engine-specific markers: nested block comments where the engine nests them, Snowflake's `//`, and `#` that is an
    // operator on PostgreSQL and a temporary table on SQL Server.
    [TestCase("SELECT 1 /* a /* b */ c */ FROM t", "PostgreSQL", "SELECT 1   FROM t")]
    [TestCase("SELECT 1 /* a /* b */ c */ FROM t", "MSSQL", "SELECT 1   FROM t")]
    [TestCase("SELECT 1 /* a /* b */ c */ FROM t", "AzureSynapse", "SELECT 1   FROM t")]
    [TestCase("// weekly\nSELECT id FROM orders", "Snowflake", " SELECT id FROM orders")]
    [TestCase("SELECT 5 # 3 AS x -- note", "PostgreSQL", "SELECT 5 # 3 AS x  ")]
    [TestCase("SELECT id FROM #recent -- note", "MSSQL", "SELECT id FROM #recent  ")]
    [TestCase("SELECT 6 // 2 AS x -- note", "PostgreSQL", "SELECT 6 // 2 AS x  ")]
    public void EachEnginesOwnMarkers_AreTheOnesRemoved(string sql, string dialect, string expected)
    {
        SqlCommentStripper.Strip(sql, dialect).Should().Be(expected);
    }

    // Offsets are measured on the text as written: line breaks, tabs and characters outside the basic plane before or inside
    // a comment do not shift what is removed.
    [TestCase("SELECT '\U0001F600'\r\n\t/* x */ FROM t", "MSSQL", "SELECT '\U0001F600'\r\n\t  FROM t")]
    [TestCase("SELECT 1 -- \U0001F600 note\r\nFROM t", "PostgreSQL", "SELECT 1  FROM t")]
    [TestCase("SELECT '\U0001F600' AS a,\r\n\t'b' AS c # \U0001F600\nFROM t", "MySQL", "SELECT '\U0001F600' AS a,\r\n\t'b' AS c  FROM t")]
    [TestCase("SELECT \"\U0001F600\"\t/* \U0001F600 */\r\n-- x\r\nFROM t", "Snowflake", "SELECT \"\U0001F600\"\t \r\n FROM t")]
    public void OffsetsAfterLineBreaksTabsAndAstralCharacters_RemoveExactlyTheComment(string sql, string dialect, string expected)
    {
        SqlCommentStripper.Strip(sql, dialect).Should().Be(expected);
    }

    // Fail closed: unusual whitespace, text that does not tokenize, a dialect without a tokenizer of its own.
    [TestCase("-- note x\nSELECT 1", "PostgreSQL")]
    [TestCase("SELECT 'never closed -- note", "PostgreSQL")]
    [TestCase("SELECT 1 -- note", "Oracle")]
    [TestCase("/* a /* b */ SELECT 1", "SQLite")]
    public void SqlTheStripperCannotReadSafely_IsReturnedAsWritten(string sql, string dialect)
    {
        SqlCommentStripper.Strip(sql, dialect).Should().Be(sql);
    }

    private static SqlGateReport Gate(string sql, string dialect)
    {
        return TestSqlGate.Create().Evaluate(new SqlGateRequest(sql, dialect, true, false, null, null, false, null, false, null, RowSecurity: TestRowSecurity.NotApplicable));
    }
}

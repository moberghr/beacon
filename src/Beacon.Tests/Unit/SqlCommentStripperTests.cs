using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// <see cref="SqlCommentStripper"/> and the regex guardrail stage that judges SQL without its comments: comments are found
/// by the dialect's tokenizer, executable comments and hints stay, and anything the stripper cannot read safely is
/// judged as written.
/// </summary>
[TestFixture]
public class SqlCommentStripperTests
{
    [TestCase("-- Revenue by month\nSELECT id FROM orders", "PostgreSQL", " SELECT id FROM orders")]
    [TestCase("/* delete old rows first */ SELECT id FROM orders", "MSSQL", "  SELECT id FROM orders")]
    [TestCase("SELECT id /* note */ FROM orders -- trailing", "Snowflake", "SELECT id   FROM orders  ")]
    [TestCase("# weekly\nSELECT id FROM orders", "MySQL", " SELECT id FROM orders")]
    public void Comments_AreReplacedByASpace(string sql, string dialect, string expected)
    {
        SqlCommentStripper.Strip(sql, dialect).Should().Be(expected);
    }

    [TestCase("SELECT '-- not a comment' AS a, '/* nor this */' AS b FROM orders", "PostgreSQL")]
    [TestCase("SELECT /*+ INDEX(orders) */ id FROM orders", "Snowflake")]
    [TestCase("SELECT 1 AS id /*!50000 DELETE FROM orders */", "PostgreSQL")]
    public void LiteralsExecutableCommentsAndHints_AreKept(string sql, string dialect)
    {
        SqlCommentStripper.Strip(sql, dialect).Should().Be(sql);
    }

    // Fail closed: the text comes back as written, and the regex stage judges it with its comments.
    [TestCase("-- x\nSELECT 1", null)]
    [TestCase("-- x\nSELECT 1", "unknown")]
    [TestCase("/* a /* b */ SELECT 1", "MySQL")]
    [TestCase("SELECT 'a\\' -- ', 1", "MySQL")]
    [TestCase("/* never closed SELECT 1", "PostgreSQL")]
    public void SqlTheStripperCannotReadSafely_IsReturnedAsWritten(string sql, string? dialect)
    {
        SqlCommentStripper.Strip(sql, dialect).Should().Be(sql);
    }

    [Test]
    public void StripLineComments_KeepsBlockComments()
    {
        SqlCommentStripper.StripLineComments("/* keep */ SELECT id -- drop\nFROM orders", "PostgreSQL")
            .Should().Be("/* keep */ SELECT id  FROM orders");
    }

    // A stored step runs on one line. Its line comments are removed first, so a filter written after a comment line is
    // still part of the statement that runs.
    [Test]
    public void AStoredStep_KeepsTheClauseAfterALineComment_WhenPutOnOneLine()
    {
        var flattened = StoredSql.Flatten("SELECT id FROM orders -- this tenant only\nWHERE tenant_id = @p0", "PostgreSQL");

        flattened.Should().Be("SELECT id FROM orders  WHERE tenant_id = @p0");
    }

    [Test]
    public void AStoredStep_OfAnUnknownDialect_IsFlattenedAsBefore()
    {
        StoredSql.Flatten("SELECT id FROM orders -- note\nWHERE a = 1", null).Should().Be("SELECT id FROM orders -- note WHERE a = 1");
    }

    [TestCase("-- Revenue\nSELECT id FROM orders")]
    [TestCase("/* delete old rows first */ SELECT id FROM orders")]
    [TestCase("SELECT id /* update weekly */ FROM orders")]
    public void TheGuardrail_JudgesTheStatement_NotItsComments(string sql)
    {
        var guardrail = new QueryGuardrailService();

        guardrail.ValidateQuery(sql, new QueryGuardrailOptions { ReadOnly = true, Dialect = "PostgreSQL" }).IsValid.Should().BeTrue();
        guardrail.ValidateQuery(sql, new QueryGuardrailOptions { ReadOnly = true }).IsValid.Should().BeFalse("without a dialect the text is judged as written");
    }

    [TestCase("/*!50000DELETE*/ FROM orders", "MySQL")]
    [TestCase("SELECT 1 /*!50000 DELETE FROM orders */", "MySQL")]
    [TestCase("SELECT 1 /*+ DELETE FROM orders */", "Snowflake")]
    [TestCase("/* report */ DELETE FROM orders", "PostgreSQL")]
    [TestCase("-- report\nDROP TABLE orders", "MSSQL")]
    public void TheGuardrail_StillRefuses_ExecutableCommentsAndWritesAfterAComment(string sql, string dialect)
    {
        new QueryGuardrailService().ValidateQuery(sql, new QueryGuardrailOptions { ReadOnly = true, Dialect = dialect }).IsValid.Should().BeFalse();
    }

    [Test]
    public void TheGate_StillRefusesAnExecutableComment_ThroughTheAstStage()
    {
        var report = TestSqlGate.Create().Evaluate(SqlGateRequest.FromSettings("SELECT 1 /*!50000 , 2 */", "MySQL", TestSqlGate.DefaultSettings()));

        report.Blocked.Should().BeTrue();
        report.Verdicts.ReadOnly.Code.Should().Be(SqlGateCodes.Ast);
    }

    // Each comment's end is measured from its own text, so characters outside the basic plane, CRLF line breaks and tabs
    // before or inside it shift nothing.
    [TestCase("SELECT '\U0001F600' AS a -- note \U0001F600\nFROM t", "PostgreSQL", "SELECT '\U0001F600' AS a  FROM t")]
    [TestCase("SELECT 1 -- note\r\nFROM t", "MSSQL", "SELECT 1  FROM t")]
    [TestCase("SELECT\t1 /* a\r\n b */\t/* c */ FROM t", "Snowflake", "SELECT\t1  \t  FROM t")]
    [TestCase("SELECT 1 /* outer /* inner */ still outer */ FROM t", "PostgreSQL", "SELECT 1   FROM t")]
    [TestCase("SELECT 1 FROM t -- last line", "BigQuery", "SELECT 1 FROM t  ")]
    public void EachCommentsEnd_IsMeasuredFromItsOwnText(string sql, string dialect, string expected)
    {
        SqlCommentStripper.Strip(sql, dialect).Should().Be(expected);
    }

    // Inside a nested comment the parser lets one character serve two markers where PostgreSQL reads each marker as two
    // characters of its own, so the two would disagree about where the comment ends. Such SQL is refused on the engines that
    // nest comments, and the stripper leaves it as written.
    [TestCase("SELECT 1 /* /* */* x */ ; DELETE FROM t; -- */", "PostgreSQL")]
    [TestCase("SELECT 1 /* a /*/ b */ c */", "MSSQL")]
    [TestCase("SELECT 1 /* /* */* x */ ; DELETE FROM t; -- */", "AzureSynapse")]
    public void ACommentMarkerSharingACharacter_IsRefused_WhereCommentsNest(string sql, string dialect)
    {
        SqlCommentStripper.Strip(sql, dialect).Should().Be(sql);
        new SqlReadOnlyAstValidator(Microsoft.Extensions.Logging.Abstractions.NullLogger<SqlReadOnlyAstValidator>.Instance)
            .Validate(sql, dialect)
            .Should().Contain("share");
        TestSqlGate.Create().Evaluate(SqlGateRequest.FromSettings(sql, dialect, TestSqlGate.DefaultSettings())).Blocked.Should().BeTrue();
    }
}

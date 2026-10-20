using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// The parser reads U+FFFF as the end of its input, so a statement holding one would be judged only up to it. Control
/// characters other than tab, LF and CR, and every Unicode noncharacter, are refused: by the gate before any regex or
/// parser sees the text, by every caller of the parser, and by the comment stripper, which leaves such SQL as written.
/// </summary>
[TestFixture]
public class SqlDisallowedCharacterTests
{
    private static readonly string[] Dialects =
        ["PostgreSQL", "MSSQL", "AzureSynapse", "MySQL", "Snowflake", "BigQuery", "Databricks", "SQLite"];

    // A comment right before the character, then a statement that ends the transaction and a write.
    private static readonly string[] Forms =
    [
        "SELECT 1 -- x￿\nCOMMIT; DELETE FROM t; SELECT 1",
        "SELECT 1 /* x */￿\nCOMMIT; DELETE FROM t; SELECT 1",
        "SELECT 1 /* x */￿ COMMIT"
    ];

    private static IEnumerable<TestCaseData> EveryDialectAndForm()
    {
        foreach (var dialect in Dialects)
        {
            for (var form = 0; form < Forms.Length; form++)
            {
                yield return new TestCaseData(dialect, Forms[form]).SetArgDisplayNames(dialect, $"form {form + 1}");
            }
        }
    }

    [TestCase("\0")]
    [TestCase("\u0001")]
    [TestCase("\u000B")]
    [TestCase("\u000C")]
    [TestCase("\u001F")]
    [TestCase("\u007F")]
    [TestCase("\u0085")]
    [TestCase("\u009F")]
    [TestCase("﷐")]
    [TestCase("﷯")]
    [TestCase("￾")]
    [TestCase("￿")]
    [TestCase("\U0001FFFE")]
    [TestCase("\U0001FFFF")]
    [TestCase("\U0010FFFE")]
    [TestCase("\U0010FFFF")]
    public void AControlCharacterOrANoncharacter_IsFound(string character)
    {
        var sql = "SELECT 1 AS a" + character + " FROM t";

        SqlAst.IndexOfDisallowedCharacter(sql).Should().Be(13);
        SqlAst.DisallowedCharacterMessageAt(13).Should().Contain("position 14");
    }

    [TestCase("\t")]
    [TestCase("\n")]
    [TestCase("\r\n")]
    [TestCase("﷏")]
    [TestCase("ﷰ")]
    [TestCase("�")]
    [TestCase("é")]
    [TestCase("\U0001F600")]
    [TestCase("\U0001FFFD")]
    public void TabsLineBreaksAndOtherText_AreAllowed(string character)
    {
        SqlAst.IndexOfDisallowedCharacter("SELECT 'a" + character + "' AS a FROM t").Should().Be(-1);
    }

    [TestCaseSource(nameof(EveryDialectAndForm))]
    public void TheGate_RefusesTheCharacter_BeforeAnyOtherStage(string dialect, string sql)
    {
        var gate = TestSqlGate.Create();

        var report = gate.Evaluate(SqlGateRequest.FromSettings(sql, dialect, new McpSettingsData()) with { MaxRows = 100 });

        report.Blocked.Should().BeTrue();
        report.Verdicts.ReadOnly.Code.Should().Be(SqlGateCodes.DisallowedCharacter);
        report.BlockReason.Should().Contain("noncharacter");
        report.FinalSql.Should().Be(sql);
        report.Verdicts.RowLimit.Code.Should().Be(SqlGateCodes.NotEvaluated);
    }

    // Read-only enforcement off, the in-memory join and a stored step put on one line are refused the same way.
    [TestCaseSource(nameof(EveryDialectAndForm))]
    public void TheCharacter_IsRefused_WithReadOnlyOff_AndAfterAStoredStepIsPutOnOneLine(string dialect, string sql)
    {
        var unlocked = TestSqlGate.Create(deployment: TestSqlGate.Unlocked);
        var readOnlyOff = SqlGateRequest.FromSettings(sql, dialect, new McpSettingsData { EnforceReadOnly = false });

        unlocked.Evaluate(readOnlyOff).Verdicts.ReadOnly.Code.Should().Be(SqlGateCodes.DisallowedCharacter);
        unlocked.Evaluate(readOnlyOff with { InMemoryJoin = true }).Blocked.Should().BeTrue();
        TestSqlGate.Create()
            .Evaluate(SqlGateRequest.FromSettings(StoredSql.Flatten(sql, dialect), dialect, new McpSettingsData()))
            .Verdicts.ReadOnly.Code.Should().Be(SqlGateCodes.DisallowedCharacter);
    }

    [TestCaseSource(nameof(EveryDialectAndForm))]
    public void EveryCallerOfTheParser_RefusesTheCharacter(string dialect, string sql)
    {
        var act = () => SqlAst.Parse(sql, dialect);

        act.Should().Throw<SqlAst.DisallowedCharacterException>();
        new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance).Validate(sql, dialect)
            .Should().Contain("noncharacter");
        SqlCrossDatabaseRule.Validate(sql, dialect, null).Should().NotBeNull();
        var capped = SqlRowLimitRewriter.Apply(sql, 100, dialect);
        capped.Outcome.Should().Be(SqlRowLimitOutcome.Refused);
        capped.Sql.Should().Be(sql);
        SqlPageRewriter.Plan(sql, dialect, 0, 50, null).PageSql.Should().BeNull();
        capped.RefusalMessage.Should().Contain("noncharacter");
    }

    // The comment stripper never ends a comment's span at the end of the input on the parser's word: the SQL comes back as
    // written, so the regex stage still sees the write after the character.
    [TestCaseSource(nameof(EveryDialectAndForm))]
    public void TheCommentStripper_LeavesSqlHoldingTheCharacterAsWritten(string dialect, string sql)
    {
        SqlCommentStripper.Strip(sql, dialect).Should().Be(sql);
        SqlCommentStripper.StripLineComments(sql, dialect).Should().Be(sql);
    }

    [TestCase("PostgreSQL")]
    [TestCase("MySQL")]
    [TestCase("Snowflake")]
    public void TheGuardrail_SeesTheWriteAfterTheCharacter(string dialect)
    {
        var result = new QueryGuardrailService().ValidateQuery(
            "SELECT 1 /* x */￿; DELETE FROM t",
            new QueryGuardrailOptions { ReadOnly = true, Dialect = dialect });

        result.IsValid.Should().BeFalse();
    }
}

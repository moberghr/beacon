using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Beacon.Core.Models;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// T-SQL needs no <c>;</c> between statements, so a word the parser reads as an unquoted alias may start the next
/// statement on SQL Server. On the T-SQL dialects (SQL Server, Azure Synapse) an unquoted select-list or table alias that is
/// such a keyword is refused; a quoted one, and the same word on other dialects, are not.
/// </summary>
[TestFixture]
public class SqlReadOnlyValidatorTSqlAliasTests
{
    private static readonly string[] Keywords =
    [
        "COMMIT", "ROLLBACK", "SAVE", "SHUTDOWN", "RECONFIGURE", "CHECKPOINT", "REVERT", "KILL", "BEGIN", "END", "GO", "EXEC",
        "EXECUTE", "USE", "DENY", "GRANT", "REVOKE", "BACKUP", "RESTORE", "DBCC", "WAITFOR", "OPEN", "CLOSE", "FETCH",
        "DEALLOCATE", "PRINT", "RAISERROR", "THROW"
    ];

    private readonly SqlReadOnlyAstValidator _validator = new(NullLogger<SqlReadOnlyAstValidator>.Instance);

    private static IEnumerable<TestCaseData> KeywordsOnTSqlDialects()
    {
        foreach (var dialect in new[] { "MSSQL", "AzureSynapse" })
        {
            foreach (var keyword in Keywords)
            {
                yield return new TestCaseData(dialect, keyword);
            }
        }
    }

    [TestCaseSource(nameof(KeywordsOnTSqlDialects))]
    public void AnUnquotedSelectListAlias_ThatIsAStatementKeyword_IsRefused(string dialect, string keyword)
    {
        var error = _validator.Validate($"SELECT 1 {keyword}", dialect);

        error.Should().NotBeNull();
        error.Should().NotContain("Only SELECT").And.NotBeNullOrEmpty();
    }

    [TestCaseSource(nameof(KeywordsOnTSqlDialects))]
    public void AnUnquotedTableAlias_ThatIsAStatementKeyword_IsRefused(string dialect, string keyword)
    {
        _validator.Validate($"SELECT * FROM dbo.t {keyword}", dialect).Should().NotBeNull();
    }

    [TestCase("SELECT 1 COMMIT", "MSSQL")]
    [TestCase("SELECT * FROM dbo.t SHUTDOWN", "MSSQL")]
    [TestCase("SELECT x.id FROM (SELECT 1 AS id) RECONFIGURE", "AzureSynapse")]
    [TestCase("SELECT 1 AS checkpoint", "MSSQL")]
    [TestCase("SELECT a.id FROM dbo.a JOIN dbo.b rollback ON rollback.id = a.id", "MSSQL")]
    public void TheRefusal_NamesTheKeywordAndHowToQuoteIt(string sql, string dialect)
    {
        _validator.Validate(sql, dialect).Should().Contain("cannot be an unquoted alias on SQL Server");
    }

    [TestCase("SELECT 1 [COMMIT]")]
    [TestCase("SELECT 1 AS [SHUTDOWN]")]
    [TestCase("SELECT 1 AS \"END\"")]
    [TestCase("SELECT [open].id FROM dbo.t [open]")]
    [TestCase("SELECT 1 AS committed, 2 AS shutdown_time")]
    [TestCase("SELECT t.id FROM dbo.t t")]
    public void AQuotedAliasOrAnOrdinaryName_IsAllowed(string sql)
    {
        _validator.Validate(sql, "MSSQL").Should().BeNull();
    }

    [TestCase("PostgreSQL", "SELECT 1 AS commit")]
    [TestCase("MySQL", "SELECT 1 AS rollback")]
    [TestCase("Snowflake", "SELECT 1 AS checkpoint")]
    public void TheSameWordOnAnotherDialect_IsNotRefusedAsAnAlias(string dialect, string sql)
    {
        (_validator.Validate(sql, dialect) ?? "").Should().NotContain("unquoted alias");
    }

    // After a statement the parser reads END as the end of a block and stops reading, on every dialect: an END that closes
    // no CASE is refused wherever the parser is used, so nothing after it goes unjudged.
    [TestCase("MSSQL", "SELECT 1 END SELECT 2")]
    [TestCase("PostgreSQL", "SELECT 1 END; SELECT 2")]
    [TestCase("MySQL", "SELECT 1 END")]
    [TestCase("Snowflake", "SELECT CASE WHEN a = 1 THEN 1 END AS x FROM t END")]
    public void AnEndThatClosesNoCase_IsRefused(string dialect, string sql)
    {
        var parse = () => SqlAst.Parse(sql, dialect);

        parse.Should().Throw<SqlAst.UnmatchedEndException>();
        _validator.Validate(sql, dialect).Should().Contain("closes no CASE");
        SqlRowLimitRewriter.Apply(sql, 10, dialect).Outcome.Should().Be(SqlRowLimitOutcome.Refused);
        SqlCrossDatabaseRule.Validate(sql, dialect, null).Should().NotBeNull();
    }

    [TestCase("MSSQL", "SELECT CASE WHEN a = 1 THEN 1 ELSE CASE WHEN b = 1 THEN 2 END END AS x, [end] FROM dbo.t")]
    [TestCase("PostgreSQL", "SELECT \"end\", CASE a WHEN 1 THEN 'x' END AS y FROM t")]
    public void CaseExpressionsAndQuotedNames_AreAllowed(string dialect, string sql)
    {
        _validator.Validate(sql, dialect).Should().BeNull();
    }

    [Test]
    public void TheGate_RefusesTheAlias_BeforeAnythingRuns()
    {
        var report = TestSqlGate.Create().Evaluate(SqlGateRequest.FromSettings("SELECT 1 COMMIT", "MSSQL", new McpSettingsData(), TestRowSecurity.NotApplicable));

        report.Blocked.Should().BeTrue();
        report.Verdicts.ReadOnly.Code.Should().Be(SqlGateCodes.Ast);
    }
}

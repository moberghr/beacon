using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Beacon.Core.Configuration;
using Beacon.Core.Models;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.Core.Validators;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Untrusted SQL must not exhaust the stack or a CPU core. The parser's recursion budget does not cover left-associative
/// chains, so the parsed tree's depth is bounded before any recursive walk; SQL text is capped in length; and comment
/// stripping is a single forward pass. The chains below are far deeper than the bound yet shallow enough that the test
/// host would survive even without it, so a regression fails an assertion instead of ending the run.
/// </summary>
[TestFixture]
public class SqlNestingBoundsTests
{
    private const int ChainLength = 1_000;

    private static readonly string LongUnion = "SELECT 1" + string.Concat(Enumerable.Repeat(" UNION SELECT 1", ChainLength));

    private static readonly string LongSum = "SELECT 1" + string.Concat(Enumerable.Repeat(" + 1", ChainLength));

    private static readonly string LongOr = "SELECT * FROM t WHERE " + string.Join(" OR ", Enumerable.Range(0, ChainLength).Select(x => $"id = {x}"));

    [TestCase("union")]
    [TestCase("sum")]
    [TestCase("or")]
    public void Validate_ChainDeeperThanTheBound_IsRejected(string chain)
    {
        var validator = new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance);

        validator.Validate(DeepChain(chain), "PostgreSQL").Should().Contain(SqlAst.TooDeepMessage);
    }

    [TestCase("union")]
    [TestCase("sum")]
    [TestCase("or")]
    public void Gate_ChainDeeperThanTheBound_IsBlockedBeforeAnyLaterStage(string chain)
    {
        var report = TestSqlGate.Create().Evaluate(SqlGateRequest.FromSettings(DeepChain(chain), "PostgreSQL", TestSqlGate.DefaultSettings()) with
        {
            MaxRows = 10
        });

        report.Blocked.Should().BeTrue();
        report.Verdicts.ReadOnly.Code.Should().Be(SqlGateCodes.Ast);
        report.Verdicts.Schema.Code.Should().Be(SqlGateCodes.NotEvaluated);
    }

    [Test]
    public void OtherParsers_ChainDeeperThanTheBound_DegradeWithoutWalkingTheTree()
    {
        new SqlSchemaValidator().Validate(LongSum, [], "PostgreSQL").Error.Should().Contain(SqlAst.TooDeepMessage);
        SqlPageRewriter.Plan(LongUnion, "PostgreSQL", 0, 50, null).PageSql.Should().BeNull();
    }

    // No textual fallback: the heuristic would take a LIMIT inside a literal for an existing cap.
    [Test]
    public void RowLimit_ChainDeeperThanTheBound_IsRefusedNotCappedTextually()
    {
        SqlRowLimitRewriter.Apply(LongUnion, 10, "PostgreSQL").Outcome.Should().Be(SqlRowLimitOutcome.Refused);
        FluentActions.Invoking(() => new QueryGuardrailService().ApplyRowLimit(LongUnion, 10, "PostgreSQL"))
            .Should().Throw<InvalidOperationException>()
            .WithMessage(SqlAst.TooDeepMessage);
    }

    // The parser's own recursion budget runs out on the parentheses; that is a depth refusal too, not a parse gap.
    private static readonly string LimitInsideDeepParentheses =
        "SELECT " + new string('(', 60) + "'LIMIT 1'" + new string(')', 60) + " AS x FROM t";

    [Test]
    public void RowLimit_ParenthesesBeyondTheParserBudget_AreRefused()
    {
        SqlRowLimitRewriter.Apply(LimitInsideDeepParentheses, 10, "PostgreSQL").Outcome.Should().Be(SqlRowLimitOutcome.Refused);
    }

    [Test]
    public void Gate_ParenthesesBeyondTheParserBudgetWithReadOnlyOff_IsBlocked()
    {
        var request = SqlGateRequest.FromSettings(LimitInsideDeepParentheses, "PostgreSQL", TestSqlGate.DefaultSettings()) with
        {
            EnforceReadOnly = false,
            MaxRows = 10
        };

        var report = TestSqlGate.Create().Evaluate(request);

        report.Blocked.Should().BeTrue();
        report.Verdicts.RowLimit.Code.Should().Be(SqlGateCodes.TooDeep);
    }

    [Test]
    public void Gate_ChainDeeperThanTheBoundWithReadOnlyOff_IsBlockedByTheRowLimitStage()
    {
        var sql = "SELECT 'LIMIT 1' AS x" + string.Concat(Enumerable.Repeat(" UNION SELECT 'LIMIT 1'", ChainLength));
        var request = SqlGateRequest.FromSettings(sql, "PostgreSQL", TestSqlGate.DefaultSettings()) with
        {
            EnforceReadOnly = false,
            MaxRows = 10
        };

        var report = TestSqlGate.Create().Evaluate(request);

        report.Blocked.Should().BeTrue();
        report.BlockReason.Should().Be(SqlAst.TooDeepMessage);
        report.Verdicts.RowLimit.Code.Should().Be(SqlGateCodes.TooDeep);
        report.FinalSql.Should().Be(sql);
    }

    [Test]
    public void Validate_LongButShallowSql_StillPasses()
    {
        var validator = new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance);
        var orChain = "SELECT * FROM t WHERE " + string.Join(" OR ", Enumerable.Range(0, 300).Select(x => $"id = {x}"));
        var inList = "SELECT * FROM t WHERE id IN (" + string.Join(", ", Enumerable.Range(0, 5_000)) + ")";

        validator.Validate(orChain, "PostgreSQL").Should().BeNull();
        validator.Validate(inList, "PostgreSQL").Should().BeNull();
    }

    [Test]
    public void Validate_ParenthesesBeyondTheParserBudget_AreRejected()
    {
        var validator = new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance);
        var sql = "SELECT " + new string('(', 200) + "1" + new string(')', 200);

        validator.Validate(sql, "PostgreSQL").Should().StartWith("Could not verify this SQL is read-only");
    }

    [Test]
    public void SqlLongerThanTheConfiguredCap_IsRejectedByBothChecks()
    {
        var options = Options.Create(new McpDeploymentOptions { Ceilings = new McpCeilingOptions { MaxSqlChars = 40 } });
        var validator = new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance, options);
        var sql = "SELECT id, name, email FROM customers WHERE id = 1";

        validator.MaxSqlChars.Should().Be(40);
        validator.Validate(sql, "PostgreSQL").Should().Be($"SQL is {sql.Length} characters long; the limit is 40.");
        FluentActions.Invoking(() => QueryValidator.CheckForFlaggedWords(sql, validator.MaxSqlChars))
            .Should().Throw<BeaconException>()
            .WithMessage($"SQL is {sql.Length} characters long; the limit is 40.");
    }

    [Test]
    public void CheckForFlaggedWords_DefaultCap_IsTheMcpSqlCap()
    {
        var sql = "SELECT " + new string('x', McpCeilingOptions.DefaultMaxSqlChars);

        FluentActions.Invoking(() => QueryValidator.CheckForFlaggedWords(sql))
            .Should().Throw<BeaconException>()
            .WithMessage("SQL is * characters long; the limit is 100000.");
    }

    // Unclosed comment openers made the old lazy regex rescan to the end of the text from every opener (quadratic).
    [TestCase("/* ")]
    [TestCase("-- /* ")]
    [TestCase("/* */ /*")]
    public void CheckForFlaggedWords_ManyCommentMarkers_CompletesInLinearTime(string marker)
    {
        var sql = "SELECT 1 " + string.Concat(Enumerable.Repeat(marker, 150_000 / marker.Length));
        QueryValidator.CheckForFlaggedWords("SELECT 1 /* warm-up */", int.MaxValue);

        var stopwatch = Stopwatch.StartNew();
        QueryValidator.CheckForFlaggedWords(sql, int.MaxValue);
        stopwatch.Stop();

        // The old lazy regex needed seconds for a fraction of this input; a second leaves room for a slow host.
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(1_000);
    }

    private static string DeepChain(string chain)
    {
        return chain switch
        {
            "union" => LongUnion,
            "sum" => LongSum,
            _ => LongOr
        };
    }
}

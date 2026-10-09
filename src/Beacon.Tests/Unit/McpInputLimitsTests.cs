using System.Diagnostics;
using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Configuration;
using Beacon.Core.Services.Validation;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC6 — <c>Beacon:Mcp:Ceilings:MaxSqlChars</c> / <c>MaxQuestionChars</c>: built-in defaults when unset, configured
/// values honoured, non-positive values fail startup validation; and the T-SQL leading-SELECT scan of
/// <see cref="SqlRowLimitRewriter"/> stays linear on a long run of leading comments.
/// </summary>
[TestFixture]
public class McpInputLimitsTests
{
    private readonly McpDeploymentOptionsValidator _validator = new();

    [Test]
    public void EffectiveLimits_Unconfigured_AreTheBuiltInDefaults()
    {
        var ceilings = new McpDeploymentOptions().Ceilings;

        ceilings.MaxSqlChars.Should().BeNull();
        ceilings.MaxQuestionChars.Should().BeNull();
        ceilings.EffectiveMaxSqlChars.Should().Be(100_000);
        ceilings.EffectiveMaxQuestionChars.Should().Be(4_000);
        _validator.Validate(null, new McpDeploymentOptions()).Succeeded.Should().BeTrue();
    }

    [Test]
    public void EffectiveLimits_Configured_AreHonoured()
    {
        var ceilings = new McpCeilingOptions { MaxSqlChars = 250_000, MaxQuestionChars = 500 };

        ceilings.EffectiveMaxSqlChars.Should().Be(250_000);
        ceilings.EffectiveMaxQuestionChars.Should().Be(500);
        _validator.Validate(null, new McpDeploymentOptions { Ceilings = ceilings }).Succeeded.Should().BeTrue();
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void NonPositiveMaxSqlChars_FailsValidation(int value)
    {
        var result = _validator.Validate(null, new McpDeploymentOptions { Ceilings = new McpCeilingOptions { MaxSqlChars = value } });

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle()
            .Which.Should().Contain("Beacon:Mcp:Ceilings:MaxSqlChars");
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void NonPositiveMaxQuestionChars_FailsValidation(int value)
    {
        var result = _validator.Validate(null, new McpDeploymentOptions { Ceilings = new McpCeilingOptions { MaxQuestionChars = value } });

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle()
            .Which.Should().Contain("Beacon:Mcp:Ceilings:MaxQuestionChars");
    }

    [Test]
    public void RowLimitRewriter_SixtyFourLeadingCommentBlocks_PlacesTopQuickly()
    {
        var leadingComments = string.Concat(Enumerable.Range(0, 64)
            .Select(x => x % 2 == 0 ? $"/* note {x} */ " : $"-- line {x}\n"));
        var sql = leadingComments + "SELECT id FROM orders";

        var stopwatch = Stopwatch.StartNew();
        var result = SqlRowLimitRewriter.Apply(sql, 100, "MSSQL");
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        result.Outcome.Should().Be(SqlRowLimitOutcome.Applied);
        result.Sql.Should().Be(leadingComments + "SELECT TOP 100 id FROM orders");
    }

    /// <summary>
    /// The leading-SELECT scan itself, on input it does NOT match: a run of empty block comments with no SELECT after
    /// it. The former <c>(?:…|/\*.*?\*/)*SELECT</c> pattern tried every way of splitting the run into comments before
    /// failing (2^29 here, seconds to minutes); <see cref="SqlRowLimitRewriter.Apply"/> cannot show that, because its
    /// textual fallback hides a slow or timed-out scan.
    /// </summary>
    [Test]
    public void RowLimitRewriter_LeadingSelectScan_NonMatchingCommentRun_StaysLinear()
    {
        var sql = string.Concat(Enumerable.Repeat("/**/", 30)) + "X";

        var stopwatch = Stopwatch.StartNew();
        var selectEnd = SqlRowLimitRewriter.FindLeadingSelectEnd(sql);
        stopwatch.Stop();

        selectEnd.Should().Be(-1);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }
}

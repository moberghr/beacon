using System.Reflection;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Services;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The model-response cleaner has one implementation (<see cref="SqlResponseCleaner"/>). The ask pipeline refuses a
/// response with no SQL in it; the cross-source join keeps whatever text it got and lets the SQL gate judge it.
/// </summary>
[TestFixture]
public class CleanSqlResponseTests
{
    private const string Fence = "```";

    private static IEnumerable<TestCaseData> Fixtures()
    {
        yield return new TestCaseData("SELECT 1", "SELECT 1", true);
        yield return new TestCaseData("  SELECT 1;  ", "SELECT 1;", true);
        yield return new TestCaseData("SELECT 1; -- done", "SELECT 1;", true);
        yield return new TestCaseData("SELECT 1;;", "SELECT 1;;", true);
        yield return new TestCaseData($"{Fence}sql\nSELECT a FROM t;\n{Fence}", "SELECT a FROM t;", true);
        yield return new TestCaseData($"{Fence}\nSELECT a FROM t\n{Fence}", "SELECT a FROM t", true);
        yield return new TestCaseData($"{Fence}sql\nSELECT a FROM t", "SELECT a FROM t", true);
        yield return new TestCaseData($"Here is the SQL:\n{Fence}sql\nSELECT a FROM t;\n{Fence}\nHope it helps.", "SELECT a FROM t;", true);
        yield return new TestCaseData($"{Fence}sql\nSELECT 1;\n{Fence}\n{Fence}sql\nSELECT 2;\n{Fence}", "SELECT 1;", true);
        yield return new TestCaseData($"{Fence}SELECT 1{Fence}", "SELECT 1", true);
        yield return new TestCaseData("Sure! Here you go: select a from t;", "select a from t;", true);
        yield return new TestCaseData("Answer: WITH x AS (SELECT 1) SELECT * FROM x; thanks", "WITH x AS (SELECT 1) SELECT * FROM x;", true);
        yield return new TestCaseData("Plan: EXPLAIN SELECT 1", "EXPLAIN SELECT 1", true);
        yield return new TestCaseData("SELECT\n1", "SELECT\n1", false);
        yield return new TestCaseData("I cannot answer that.", "I cannot answer that.", false);
        yield return new TestCaseData("no sql; at all", "no sql;", false);
        yield return new TestCaseData("   ", "", false);
        yield return new TestCaseData("", "", false);
    }

    [TestCaseSource(nameof(Fixtures))]
    public void Clean_ReturnsTheSqlAndSaysWhetherAnyWasFound(string content, string expected, bool found)
    {
        var sql = SqlResponseCleaner.Clean(content, out var foundSql);

        sql.Should().Be(expected);
        foundSql.Should().Be(found || content.Contains(Fence));
    }

    [TestCaseSource(nameof(Fixtures))]
    public void AskPipeline_KeepsItsOutputAndRefusals(string content, string expected, bool found)
    {
        var type = typeof(SqlGenerationService);
        var method = type.GetMethod("CleanSqlResponse", BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        Func<object?> act = () => method!.Invoke(null, [content]);

        if (string.IsNullOrWhiteSpace(content) || (!found && !content.Contains(Fence)))
        {
            act.Should().Throw<TargetInvocationException>().WithInnerException<Beacon.Core.Exceptions.AiServiceException>();
            return;
        }

        act().Should().Be(expected);
    }

    [TestCaseSource(nameof(Fixtures))]
    public void CrossSourceJoin_KeepsItsOutputWithoutRefusing(string content, string expected, bool found)
    {
        var type = typeof(Beacon.MCP.Services.CrossSourceQueryService).Assembly.GetType("Beacon.MCP.Services.SqlParsingHelper");
        var method = type!.GetMethod("CleanSqlResponse", BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        method!.Invoke(null, [content]).Should().Be(expected);
    }
}

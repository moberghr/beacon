using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;

namespace Beacon.Tests.Unit;

[TestFixture]
public class DataQualitySqlGeneratorTests
{
    private DataQualitySqlGenerator _generator = null!;

    [SetUp]
    public void SetUp() => _generator = new DataQualitySqlGenerator();

    private static DataContractRule RangeRule(string configJson) =>
        new()
        {
            Name = "range",
            RuleType = DataContractRuleType.Range,
            Configuration = configJson,
        };

    [Test]
    public void GenerateSql_RangeRule_ValidNumericBounds_EmitsBoundsVerbatim()
    {
        var rule = RangeRule("""{"schema":"public","table":"orders","column":"amount","min":"0","max":"100"}""");

        var sql = _generator.GenerateSql(rule, DatabaseEngineType.PostgreSQL);

        sql.Should().Contain("\"amount\" < 0");
        sql.Should().Contain("\"amount\" > 100");
    }

    // §1.10 — min/max are interpolated into SQL (they cannot be parameterized through this path),
    // so a non-numeric value must be rejected before it reaches the query string.
    [TestCase("1 OR 1=1")]
    [TestCase("0); DROP TABLE orders;--")]
    [TestCase("(SELECT 1)")]
    [TestCase("abc")]
    public void GenerateSql_RangeRule_NonNumericMin_IsRejected(string maliciousMin)
    {
        var rule = RangeRule($$"""{"schema":"public","table":"orders","column":"amount","min":"{{maliciousMin}}"}""");

        var act = () => _generator.GenerateSql(rule, DatabaseEngineType.PostgreSQL);

        act.Should().Throw<InvalidOperationException>().WithMessage("*numeric*");
    }

    [TestCase("99 OR 1=1")]
    [TestCase("100); DELETE FROM orders;--")]
    public void GenerateSql_RangeRule_NonNumericMax_IsRejected(string maliciousMax)
    {
        var rule = RangeRule($$"""{"schema":"public","table":"orders","column":"amount","min":"0","max":"{{maliciousMax}}"}""");

        var act = () => _generator.GenerateSql(rule, DatabaseEngineType.PostgreSQL);

        act.Should().Throw<InvalidOperationException>().WithMessage("*numeric*");
    }

    [TestCase(123.45)]
    [TestCase(-7)]
    public void GenerateSql_RangeRule_NegativeAndDecimalBounds_AreAccepted(decimal bound)
    {
        var rule = RangeRule($$"""{"schema":"public","table":"orders","column":"amount","min":"{{bound}}"}""");

        var act = () => _generator.GenerateSql(rule, DatabaseEngineType.PostgreSQL);

        act.Should().NotThrow();
    }

    [Test]
    public void GenerateSql_RangeRule_NonIdentifierColumn_IsRejected()
    {
        var rule = RangeRule("""{"schema":"public","table":"orders","column":"amount; DROP TABLE orders","min":"0"}""");

        var act = () => _generator.GenerateSql(rule, DatabaseEngineType.PostgreSQL);

        act.Should().Throw<InvalidOperationException>();
    }
}

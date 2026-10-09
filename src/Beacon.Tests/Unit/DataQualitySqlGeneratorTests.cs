using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;
using Beacon.Tests.Common;

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

        var sql = _generator.GenerateSql(rule, DatabaseEngineType.PostgreSQL).Sql;

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

    [TestCase("123.45", "123.45")]
    [TestCase("-7", "-7")]
    [TestCase("+5", "5")]
    [TestCase("007", "7")]
    [TestCase(".5", "0.5")]
    public void GenerateSql_RangeRule_PlainNumbers_AreWrittenAsParsed(string bound, string written)
    {
        var rule = RangeRule($$"""{"schema":"public","table":"orders","column":"amount","min":"{{bound}}"}""");

        var sql = _generator.GenerateSql(rule, DatabaseEngineType.PostgreSQL).Sql;

        sql.Should().EndWith($"WHERE \"amount\" < {written}", "the SQL gets the parsed value, never the input text");
    }

    // Only a plain number is accepted: whitespace, thousands separators, a trailing sign or an exponent would reach the
    // SQL text as written.
    [TestCase("1,000")]
    [TestCase("5-")]
    [TestCase(" 5")]
    [TestCase("5 ")]
    [TestCase("5\n")]
    [TestCase("1e3")]
    [TestCase("0x10")]
    [TestCase("(5)")]
    public void GenerateSql_RangeRule_NonPlainNumber_IsRejected(string bound)
    {
        var rule = RangeRule(JsonSerializer.Serialize(new { schema = "public", table = "orders", column = "amount", min = bound }));

        var act = () => _generator.GenerateSql(rule, DatabaseEngineType.PostgreSQL);

        act.Should().Throw<InvalidOperationException>().WithMessage("*numeric*");
    }

    // T-SQL writes the bound as "-{n}", so a negative value would open a "--" comment.
    [TestCase(DatabaseEngineType.MSSQL, 0)]
    [TestCase(DatabaseEngineType.MSSQL, -5)]
    [TestCase(DatabaseEngineType.PostgreSQL, -5)]
    public void GenerateSql_FreshnessRule_NonPositiveMaxAge_IsRejected(DatabaseEngineType engine, int maxAgeMinutes)
    {
        var rule = new DataContractRule
        {
            Name = "freshness",
            RuleType = DataContractRuleType.Freshness,
            Configuration = JsonSerializer.Serialize(new { schema = "public", table = "orders", column = "updated_at", maxAgeMinutes })
        };

        var act = () => _generator.GenerateSql(rule, engine);

        act.Should().Throw<InvalidOperationException>().WithMessage("*'maxAgeMinutes' must be greater than zero*");
    }

    [TestCase("orders\n")]
    [TestCase("orders\r\n")]
    public void GenerateSql_TableNameWithATrailingNewline_IsRejected(string table)
    {
        var rule = new DataContractRule
        {
            Name = "volume",
            RuleType = DataContractRuleType.Volume,
            Configuration = JsonSerializer.Serialize(new { schema = "public", table })
        };

        var act = () => _generator.GenerateSql(rule, DatabaseEngineType.PostgreSQL);

        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void GenerateSql_RangeRule_NonIdentifierColumn_IsRejected()
    {
        var rule = RangeRule("""{"schema":"public","table":"orders","column":"amount; DROP TABLE orders","min":"0"}""");

        var act = () => _generator.GenerateSql(rule, DatabaseEngineType.PostgreSQL);

        act.Should().Throw<InvalidOperationException>();
    }

    private static DataContractRule PatternRule(string configJson) =>
        new()
        {
            Name = "pattern",
            RuleType = DataContractRuleType.Pattern,
            Configuration = configJson,
        };

    // §1.10 — the Pattern rule interpolates the column identifier directly, so it must be
    // whitelist-validated exactly like the other rule types (regression guard for the fix).
    [TestCase("amount; DROP TABLE orders")]
    [TestCase("col FROM users--")]
    [TestCase("secret FROM users")]
    public void GenerateSql_PatternRule_NonIdentifierColumn_IsRejected(string maliciousColumn)
    {
        var config = $$"""{"schema":"public","table":"orders","column":"{{maliciousColumn}}","pattern":"^[0-9]+$"}""";
        var rule = PatternRule(config);

        var act = () => _generator.GenerateSql(rule, DatabaseEngineType.PostgreSQL);

        act.Should().Throw<InvalidOperationException>();
    }

    // §1.10 — the pattern is a user value: it is bound as a database parameter, never written into the SQL, so neither a
    // quote nor a backslash (an escape character in MySQL string literals by default) can end a literal early.
    [TestCase(DatabaseEngineType.PostgreSQL, "\"code\" !~ @p0")]
    [TestCase(DatabaseEngineType.MySQL, "`code` NOT REGEXP @p0")]
    [TestCase(DatabaseEngineType.MSSQL, "[code] NOT LIKE @p0")]
    public void GenerateSql_PatternRule_BindsThePatternAsAParameter(DatabaseEngineType engine, string predicate)
    {
        const string pattern = @"^A\' or 'B''\\$";
        var config = JsonSerializer.Serialize(new { schema = "public", table = "orders", column = "code", pattern });

        var query = _generator.GenerateSql(PatternRule(config), engine);

        query.Sql.Should().EndWith(predicate);
        query.Sql.Should().NotContain("'", "no part of the pattern reaches the SQL text");
        query.Sql.Should().NotContain(@"\");
        query.Parameters.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, object?>("p0", pattern), "the pattern is bound verbatim");
    }

    [TestCase(DataContractRuleType.Freshness, """{"column":"updated_at","maxAgeMinutes":60}""")]
    [TestCase(DataContractRuleType.Volume, """{"minRows":1}""")]
    [TestCase(DataContractRuleType.NullRate, """{"column":"email","maxNullPercent":5}""")]
    [TestCase(DataContractRuleType.Uniqueness, """{"column":"id"}""")]
    [TestCase(DataContractRuleType.Referential, """{"column":"customer_id","referenceTable":"customers","referenceColumn":"id"}""")]
    [TestCase(DataContractRuleType.Range, """{"column":"amount","min":"0","max":"100"}""")]
    [TestCase(DataContractRuleType.Pattern, """{"column":"code","pattern":"^[A-Z]+$"}""")]
    public void GenerateSql_EveryRuleType_PassesTheReadOnlyGateForEachEngine(DataContractRuleType ruleType, string configJson)
    {
        var gate = TestSqlGate.Create();

        foreach (var engine in new[] { DatabaseEngineType.PostgreSQL, DatabaseEngineType.MSSQL, DatabaseEngineType.MySQL })
        {
            var rule = new DataContractRule
            {
                Name = "rule",
                RuleType = ruleType,
                Configuration = WithTable(configJson)
            };
            var query = _generator.GenerateSql(rule, engine);

            var report = gate.Evaluate(DataQualityRuleGuard.GateRequest(query.Sql, engine, hostManagedKey: null));

            DataQualityRuleGuard.RejectionOf(report).Should().BeNull($"the generated {ruleType} SQL for {engine} must run, capped at one row");
        }
    }

    private static string WithTable(string configJson)
    {
        return configJson.Replace("{", """{"schema":"sales","table":"orders",""", StringComparison.Ordinal);
    }
}

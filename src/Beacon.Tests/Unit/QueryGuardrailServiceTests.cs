using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Services.Security;

namespace Beacon.Tests.Unit;

[TestFixture]
public class QueryGuardrailServiceTests
{
    private QueryGuardrailService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _service = new QueryGuardrailService();
    }

    [TestCase("SELECT * FROM orders")]
    [TestCase("WITH r AS (SELECT 1) SELECT * FROM r")]
    [TestCase("EXPLAIN SELECT id FROM customers")]
    public void ValidateQuery_ReadOnlySelect_IsValid(string sql)
    {
        var result = _service.ValidateQuery(sql, new QueryGuardrailOptions { ReadOnly = true, DetectPii = false });

        result.IsValid.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    [TestCase("INSERT INTO orders (id) VALUES (1)")]
    [TestCase("UPDATE orders SET status = 'x'")]
    [TestCase("DELETE FROM orders")]
    [TestCase("DROP TABLE orders")]
    [TestCase("TRUNCATE TABLE orders")]
    [TestCase("MERGE INTO t USING s ON t.id = s.id WHEN MATCHED THEN UPDATE SET v = 1")]
    public void ValidateQuery_WriteOperations_AreRejected(string sql)
    {
        var result = _service.ValidateQuery(sql, new QueryGuardrailOptions { ReadOnly = true });

        result.IsValid.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void ValidateQuery_StackedWriteAfterSelect_IsRejected()
    {
        var result = _service.ValidateQuery("SELECT 1; DROP TABLE orders", new QueryGuardrailOptions { ReadOnly = true });

        result.IsValid.Should().BeFalse();
    }

    [Test]
    public void ValidateQuery_UnclosedCommentBomb_128Kb_ValidatesInUnderOneSecond()
    {
        // 2026-10-08 audit: the comment alternative of the dangerous-pattern regex backtracked super-linearly on
        // an unclosed comment run (16 KB took 1.7 s). Keyword-free, so the regex stage must pass it — quickly.
        var sql = "SELECT 1 " + string.Concat(Enumerable.Repeat("/* ", 128 * 1024 / 3));

        var stopwatch = Stopwatch.StartNew();
        var result = _service.ValidateQuery(sql, new QueryGuardrailOptions { ReadOnly = true, DetectPii = true });
        stopwatch.Stop();

        result.IsValid.Should().BeTrue("the input carries no write keyword; the AST stage rejects the unclosed comment");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    // Boundary: everything the regex stage rejected before the ReDoS fix is still rejected — the stacked-statement
    // form (no FROM, so only the stacked alternative sees it) and a write keyword split out of a MySQL executable
    // comment (no write form WriteOperationPattern recognises, so only the comment alternative sees it).
    [Test]
    public void EveryStaticPattern_HasAMatchTimeout()
    {
        // A pattern added later without the timeout argument gets Regex.InfiniteMatchTimeout, and IsMatchFailClosed's
        // backstop silently stops applying to it.
        var patterns = typeof(QueryGuardrailService)
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(x => x.FieldType == typeof(Regex))
            .Select(x => (Name: x.Name, Pattern: (Regex)x.GetValue(null)!))
            .ToList();

        patterns.Should().HaveCountGreaterThanOrEqualTo(5, "the guardrail's own patterns must be found, or this check is vacuous");
        patterns.Should().OnlyContain(
            x => x.Pattern.MatchTimeout != Regex.InfiniteMatchTimeout,
            "every guardrail pattern must be built with a match timeout");
    }

    [TestCase("SELECT 1; DROP TABLE orders")]
    [TestCase("SELECT 1;\n\tDELETE orders")]
    [TestCase("SELECT 1 /*!50000;*/ /*!50000DELETE*/ FROM orders")]
    [TestCase("SELECT 1 /* x */ ; /* y */ DROP TABLE orders")]
    public void ValidateQuery_StackedOrCommentHiddenWrite_IsStillRejected(string sql)
    {
        var result = _service.ValidateQuery(sql, new QueryGuardrailOptions { ReadOnly = true });

        result.IsValid.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [TestCase("SELECT 1;\n\tDELETE orders")]
    [TestCase("SELECT 1 /*!50000;*/ /*!50000DELETE*/ FROM orders")]
    public void ValidateQuery_WriteOnlyTheDangerousPatternSees_IsRejectedAsDangerous(string sql)
    {
        var result = _service.ValidateQuery(sql, new QueryGuardrailOptions { ReadOnly = true });

        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("Query contains potentially dangerous patterns.");
    }

    [Test]
    public void ValidateQuery_CommentHiddenWriteCheck_RejectsExactlyWhatTheFormerAlternativeRejected()
    {
        // The linear rewrite must accept the same language as the dropped super-linear alternative — nothing that
        // was rejected is now admitted, nothing new is rejected. The generated tails hold no whitespace and no `;`,
        // so neither WriteOperationPattern nor the stacked-statement check can fire: the comment check alone decides.
        var former = new Regex(@"/\*.*?(INSERT|UPDATE|DELETE|DROP).*?\*/", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        string[] tokens = ["/*", "*/", "/", "*", "x", "DROP", "delete", "Update", "INSERT", "DEL", "UPD", "é"];
        var random = new Random(20261008);
        var options = new QueryGuardrailOptions { ReadOnly = true, DetectPii = false };

        for (var i = 0; i < 2_000; i++)
        {
            var tail = string.Concat(Enumerable.Range(0, random.Next(1, 24)).Select(_ => tokens[random.Next(tokens.Length)]));
            var sql = "SELECT " + tail;

            _service.ValidateQuery(sql, options).IsValid
                .Should().Be(!former.IsMatch(sql), "input #{0}: {1}", i, sql);
        }
    }

    [Test]
    public void IsMatchFailClosed_PatternThatTimesOut_ReturnsTrue()
    {
        // Catastrophic backtracking under a 1 ms budget: the seam every guardrail match goes through must read a
        // timeout as "dangerous", never as a pass (§1.5 fail closed).
        var catastrophic = new Regex(@"^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(1));

        QueryGuardrailService.IsMatchFailClosed(catastrophic, new string('a', 64) + "!").Should().BeTrue();
    }

    [Test]
    public void IsMatchFailClosed_PatternThatCompletes_ReturnsTheMatchResult()
    {
        var pattern = new Regex(@"\bDROP\b", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));

        QueryGuardrailService.IsMatchFailClosed(pattern, "SELECT 1").Should().BeFalse();
        QueryGuardrailService.IsMatchFailClosed(pattern, "drop table x").Should().BeTrue();
    }

    [Test]
    public void ValidateQuery_NonSelectStart_IsRejected()
    {
        var result = _service.ValidateQuery("VALUES (1)", new QueryGuardrailOptions { ReadOnly = true });

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("SELECT");
    }

    [Test]
    public void ValidateQuery_WriteAllowed_WhenReadOnlyDisabled()
    {
        var result = _service.ValidateQuery("UPDATE orders SET status = 'x'", new QueryGuardrailOptions { ReadOnly = false, DetectPii = false });

        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void ValidateQuery_EmptySql_IsRejected()
    {
        var result = _service.ValidateQuery("   ");

        result.IsValid.Should().BeFalse();
    }

    [Test]
    public void ValidateQuery_DetectsKnownPiiColumns()
    {
        var result = _service.ValidateQuery("SELECT email, ssn, name FROM customers", new QueryGuardrailOptions { ReadOnly = true, DetectPii = true });

        result.IsValid.Should().BeTrue();
        result.PiiColumns.Should().Contain("email");
        result.PiiColumns.Should().Contain("ssn");
        result.PiiColumns.Should().NotContain("name");
    }

    [Test]
    public void ValidateQuery_DetectsCustomPiiPattern()
    {
        var options = new QueryGuardrailOptions
        {
            ReadOnly = true,
            DetectPii = true,
            CustomPiiPatterns = ["customer_secret"]
        };

        var result = _service.ValidateQuery("SELECT customer_secret FROM accounts", options);

        result.PiiColumns.Should().Contain("customer_secret");
    }

    [Test]
    public void ValidateQuery_InvalidCustomPiiPattern_IsSkippedWithoutThrowing()
    {
        var options = new QueryGuardrailOptions
        {
            ReadOnly = true,
            DetectPii = true,
            CustomPiiPatterns = ["("]
        };

        var act = () => _service.ValidateQuery("SELECT id FROM orders", options);

        act.Should().NotThrow();
    }

    [Test]
    public void ApplyRowLimit_PostgreSql_AppendsLimit()
    {
        var sql = _service.ApplyRowLimit("SELECT * FROM orders", 500, "PostgreSQL");

        sql.Should().Be("SELECT * FROM orders LIMIT 500");
    }

    [Test]
    public void ApplyRowLimit_SqlServer_InsertsTop()
    {
        var sql = _service.ApplyRowLimit("SELECT * FROM orders", 500, "MSSQL");

        sql.Should().Contain("SELECT TOP 500");
    }

    [Test]
    public void ApplyRowLimit_SqlServerWithOrderBy_UsesFetch()
    {
        var sql = _service.ApplyRowLimit("SELECT * FROM orders ORDER BY id", 500, "MSSQL");

        sql.Should().Contain("FETCH NEXT 500 ROWS ONLY");
    }

    [Test]
    public void ApplyRowLimit_ExistingLimit_IsLeftUnchanged()
    {
        var sql = _service.ApplyRowLimit("SELECT * FROM orders LIMIT 10", 500, "PostgreSQL");

        sql.Should().Be("SELECT * FROM orders LIMIT 10");
    }

    [Test]
    public void ApplyRowLimit_SqlServer_Subquery_OnlyOutermostGetsTop()
    {
        var sql = _service.ApplyRowLimit(
            "SELECT category, COUNT(*) FROM (SELECT id, category FROM orders) x GROUP BY category",
            500,
            "MSSQL");

        // Only the outer SELECT is capped — the inner subquery must NOT get TOP, or its rows would be
        // truncated before aggregation, corrupting the COUNT.
        sql.Should().StartWith("SELECT TOP 500 category");
        sql.Should().NotContain("(SELECT TOP");
    }

    [Test]
    public void ApplyRowLimit_AzureSynapse_InsertsTop()
    {
        var sql = _service.ApplyRowLimit("SELECT * FROM sales", 100, "AzureSynapse");

        // Azure Synapse is T-SQL: it must use TOP, never LIMIT (which Synapse rejects).
        sql.Should().Contain("SELECT TOP 100");
        sql.Should().NotContain("LIMIT");
    }

    [Test]
    public void ApplyRowLimit_AzureSynapseWithOrderBy_UsesFetch()
    {
        var sql = _service.ApplyRowLimit("SELECT * FROM sales ORDER BY id", 100, "AzureSynapse");

        sql.Should().Contain("FETCH NEXT 100 ROWS ONLY");
        sql.Should().NotContain("LIMIT");
    }

    [Test]
    public void ApplyRowLimit_SqlServer_CteLeading_BoundsOuterResultWithoutTop()
    {
        var sql = _service.ApplyRowLimit(
            "WITH r AS (SELECT id, category FROM orders) SELECT category, COUNT(*) AS n FROM r GROUP BY category",
            500,
            "MSSQL");

        // A first-SELECT TOP would land on the CTE body and leave the OUTER result uncapped; bound the
        // outer result with OFFSET/FETCH instead, and never inject TOP into the CTE.
        sql.Should().Contain("FETCH NEXT 500 ROWS ONLY");
        sql.Should().NotContain("TOP");
    }

    [Test]
    public void ApplyRowLimit_AzureSynapse_CteLeading_BoundsOuterResultWithoutTop()
    {
        var sql = _service.ApplyRowLimit(
            "WITH r AS (SELECT id, category FROM orders) SELECT category, COUNT(*) AS n FROM r GROUP BY category",
            500,
            "AzureSynapse");

        sql.Should().Contain("FETCH NEXT 500 ROWS ONLY");
        sql.Should().NotContain("TOP");
        sql.Should().NotContain("LIMIT");
    }

    [Test]
    public void ApplyRowLimit_SqlServerDistinct_PutsTopAfterDistinct()
    {
        var sql = _service.ApplyRowLimit("SELECT DISTINCT category FROM orders", 500, "MSSQL");

        // T-SQL requires `SELECT DISTINCT TOP n`; `TOP n DISTINCT` is a syntax error.
        sql.Should().Be("SELECT DISTINCT TOP 500 category FROM orders");
    }

    [Test]
    public void ApplyRowLimit_PostgreSqlSubqueryLimit_StillCapsOuter()
    {
        var sql = _service.ApplyRowLimit("SELECT * FROM orders WHERE id IN (SELECT id FROM customers LIMIT 5)", 500, "PostgreSQL");

        // A LIMIT in a subquery bounds an intermediate result, not what the caller receives.
        sql.Should().EndWith(" LIMIT 500");
    }

    [Test]
    public void ApplyRowLimit_PostgreSqlTrailingLineComment_AppendsOnNewLine()
    {
        var sql = _service.ApplyRowLimit("SELECT * FROM orders -- all rows", 500, "PostgreSQL");

        // Appending on the same line would put the clause inside the comment.
        sql.Should().Be("SELECT * FROM orders -- all rows\nLIMIT 500");
    }

    [TestCase("email", true)]
    [TestCase("user_password", true)]
    [TestCase("credit_card", true)]
    [TestCase("display_name", false)]
    [TestCase("order_total", false)]
    public void IsPiiColumn_MatchesKnownPatterns(string column, bool expected)
    {
        _service.IsPiiColumn(column).Should().Be(expected);
    }

    [Test]
    public void IsPiiColumn_HonorsCustomPatterns()
    {
        _service.IsPiiColumn("loyalty_pin", ["loyalty_pin"]).Should().BeTrue();
    }

    [Test]
    public void MaskPiiValues_MasksOnlyPiiColumns()
    {
        var row = new Dictionary<string, object?>
        {
            ["email"] = "alice@example.com",
            ["name"] = "Alice"
        };

        var masked = _service.MaskPiiValues(row, ["email"]);

        masked["email"].Should().NotBe("alice@example.com");
        masked["email"]!.ToString().Should().Contain("***");
        masked["name"].Should().Be("Alice");
    }
}

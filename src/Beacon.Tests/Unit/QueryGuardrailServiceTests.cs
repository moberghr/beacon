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

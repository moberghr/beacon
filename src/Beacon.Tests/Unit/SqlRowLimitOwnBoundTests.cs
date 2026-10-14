using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Models;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// The row cap holds whatever bound the statement sets itself. A bound counts only when it is a whole number no larger
/// than the cap; LIMIT ALL / NULL, a number above the cap, an expression or placeholder, TOP … PERCENT and WITH TIES do
/// not. A single-token count is lowered to the cap in place (ORDER BY and formatting kept); any other bound wraps the
/// statement in a derived table capped by an outer bound; a T-SQL statement with a WITH clause cannot be wrapped and is
/// refused. A bound inside a subquery never counts for the outer result.
/// </summary>
[TestFixture]
public class SqlRowLimitOwnBoundTests
{
    private const int Cap = 100;

    [TestCase("PostgreSQL", "SELECT * FROM t LIMIT ALL", "SELECT * FROM t LIMIT 100")]
    [TestCase("PostgreSQL", "SELECT * FROM t LIMIT NULL", "SELECT * FROM t LIMIT 100")]
    [TestCase("PostgreSQL", "SELECT * FROM t ORDER BY id LIMIT 1000000", "SELECT * FROM t ORDER BY id LIMIT 100")]
    [TestCase("PostgreSQL", "SELECT * FROM t LIMIT 1e9;", "SELECT * FROM t LIMIT 100;")]
    [TestCase("PostgreSQL", "SELECT * FROM t LIMIT 1000000 OFFSET 20", "SELECT * FROM t LIMIT 100 OFFSET 20")]
    [TestCase("PostgreSQL", "SELECT * FROM t ORDER BY id FETCH FIRST 1000000 ROWS ONLY", "SELECT * FROM t ORDER BY id FETCH FIRST 100 ROWS ONLY")]
    [TestCase("MySQL", "SELECT * FROM t LIMIT 5, 1000000", "SELECT * FROM t LIMIT 5, 100")]
    [TestCase("MySQL", "SELECT * FROM t LIMIT 1000000 OFFSET 5", "SELECT * FROM t LIMIT 100 OFFSET 5")]
    [TestCase("MSSQL", "SELECT TOP 1000000 * FROM dbo.t", "SELECT TOP 100 * FROM dbo.t")]
    [TestCase("MSSQL", "SELECT TOP (2147483647) * FROM dbo.t", "SELECT TOP (100) * FROM dbo.t")]
    [TestCase("MSSQL", "SELECT DISTINCT TOP 1000000 a FROM dbo.t", "SELECT DISTINCT TOP 100 a FROM dbo.t")]
    [TestCase("MSSQL", "SELECT a FROM dbo.t ORDER BY a OFFSET 0 ROWS FETCH NEXT 1000000 ROWS ONLY", "SELECT a FROM dbo.t ORDER BY a OFFSET 0 ROWS FETCH NEXT 100 ROWS ONLY")]
    [TestCase("MSSQL", "WITH x AS (SELECT TOP 5 a FROM dbo.t) SELECT TOP 1000000 a FROM x", "WITH x AS (SELECT TOP 5 a FROM dbo.t) SELECT TOP 100 a FROM x")]
    [TestCase("AzureSynapse", "SELECT TOP 1000000 * FROM dbo.t", "SELECT TOP 100 * FROM dbo.t")]
    [TestCase("Snowflake", "SELECT * FROM t LIMIT NULL", "SELECT * FROM t LIMIT 100")]
    [TestCase("Snowflake", "SELECT TOP 1000000 * FROM t", "SELECT TOP 100 * FROM t")]
    [TestCase("BigQuery", "SELECT * FROM d.t LIMIT 9223372036854775807", "SELECT * FROM d.t LIMIT 100")]
    [TestCase("Databricks", "SELECT * FROM t LIMIT ALL", "SELECT * FROM t LIMIT 100")]
    [TestCase("SQLite", "SELECT * FROM t LIMIT 1000000", "SELECT * FROM t LIMIT 100")]
    public void ASingleTokenBoundAboveTheCap_IsLoweredInPlace(string dialect, string sql, string expected)
    {
        var result = SqlRowLimitRewriter.Apply(sql, Cap, dialect);

        result.Outcome.Should().Be(SqlRowLimitOutcome.OwnBoundCapped);
        result.Sql.Should().Be(expected);
    }

    [TestCase("PostgreSQL", "SELECT * FROM t LIMIT $1")]
    [TestCase("PostgreSQL", "SELECT * FROM t LIMIT (SELECT 1000000)")]
    [TestCase("PostgreSQL", "SELECT * FROM t ORDER BY a FETCH FIRST 10 ROWS WITH TIES")]
    [TestCase("SQLite", "SELECT * FROM t LIMIT -1")]
    [TestCase("Databricks", "SELECT * FROM t LIMIT 10 * 100000")]
    public void AnyOtherBound_IsWrappedInACappedDerivedTable(string dialect, string sql)
    {
        var result = SqlRowLimitRewriter.Apply(sql, Cap, dialect);

        result.Outcome.Should().Be(SqlRowLimitOutcome.OwnBoundCapped);
        result.Sql.Should().Be($"SELECT * FROM (\n{sql}\n) AS beacon_rows LIMIT 100");
    }

    [TestCase("MSSQL", "SELECT TOP 100 PERCENT * FROM dbo.t")]
    [TestCase("MSSQL", "SELECT TOP 5 WITH TIES * FROM dbo.t ORDER BY a")]
    [TestCase("MSSQL", "SELECT TOP (@n) * FROM dbo.t")]
    [TestCase("AzureSynapse", "SELECT TOP 50 PERCENT a FROM dbo.t ORDER BY a")]
    public void AnyOtherTSqlBound_IsWrappedUnderTop(string dialect, string sql)
    {
        var result = SqlRowLimitRewriter.Apply(sql, Cap, dialect);

        result.Outcome.Should().Be(SqlRowLimitOutcome.OwnBoundCapped);
        result.Sql.Should().Be($"SELECT TOP 100 * FROM (\n{sql}\n) AS beacon_rows");
    }

    // T-SQL cannot put a WITH clause inside a derived table, so a bound that cannot be lowered there blocks the statement.
    [Test]
    public void ATSqlBoundThatCannotBeLoweredOrWrapped_IsRefusedByTheGate()
    {
        const string sql = "WITH x AS (SELECT a FROM dbo.t) SELECT TOP 100 PERCENT a FROM x";

        SqlRowLimitRewriter.Apply(sql, Cap, "MSSQL").Outcome.Should().Be(SqlRowLimitOutcome.Refused);

        var report = TestSqlGate.Create().Evaluate(SqlGateRequest.FromSettings(sql, "MSSQL", new McpSettingsData()) with { MaxRows = Cap });

        report.Blocked.Should().BeTrue();
        report.Verdicts.RowLimit.Code.Should().Be(SqlGateCodes.UncappableBound);
        report.BlockReason.Should().Contain("at most 100 rows");
    }

    [TestCase("PostgreSQL", "SELECT * FROM t LIMIT 50")]
    [TestCase("PostgreSQL", "SELECT * FROM t LIMIT 100")]
    [TestCase("PostgreSQL", "SELECT * FROM t FETCH FIRST ROW ONLY")]
    [TestCase("MySQL", "SELECT * FROM t LIMIT 1000000, 10")]
    [TestCase("MSSQL", "SELECT TOP 50 * FROM dbo.t")]
    [TestCase("MSSQL", "SELECT TOP (50) * FROM dbo.t")]
    [TestCase("Snowflake", "SELECT TOP 1000000 * FROM t LIMIT 10")]
    public void ABoundWithinTheCap_IsKept(string dialect, string sql)
    {
        var result = SqlRowLimitRewriter.Apply(sql, Cap, dialect);

        result.Outcome.Should().Be(SqlRowLimitOutcome.AlreadyLimited);
        result.Sql.Should().Be(sql);
    }

    [TestCase("PostgreSQL", "SELECT * FROM (SELECT * FROM t LIMIT 1000000) s", "SELECT * FROM (SELECT * FROM t LIMIT 1000000) s LIMIT 100")]
    [TestCase("MSSQL", "SELECT a FROM (SELECT TOP 1000000 a FROM dbo.t) s", "SELECT TOP 100 a FROM (SELECT TOP 1000000 a FROM dbo.t) s")]
    public void ABoundInsideASubquery_DoesNotCountForTheOuterResult(string dialect, string sql, string expected)
    {
        var result = SqlRowLimitRewriter.Apply(sql, Cap, dialect);

        result.Outcome.Should().Be(SqlRowLimitOutcome.Applied);
        result.Sql.Should().Be(expected);
    }

    // The gate reports a lowered or wrapped bound as its own verdict, and the SQL that runs is the capped one.
    [Test]
    public void TheGate_RunsTheLoweredStatement()
    {
        var report = TestSqlGate.Create().Evaluate(
            SqlGateRequest.FromSettings("SELECT id FROM orders LIMIT 5000", "PostgreSQL", new McpSettingsData()) with { MaxRows = Cap });

        report.Blocked.Should().BeFalse();
        report.Verdicts.RowLimit.Code.Should().Be(SqlGateCodes.OwnBoundCapped);
        report.FinalSql.Should().Be("SELECT id FROM orders LIMIT 100");
    }
}

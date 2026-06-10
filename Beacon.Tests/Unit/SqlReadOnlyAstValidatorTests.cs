using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Beacon.MCP.Services;

namespace Beacon.Tests.Unit;

[TestFixture]
public class SqlReadOnlyAstValidatorTests
{
    private SqlReadOnlyAstValidator _validator = null!;

    [SetUp]
    public void SetUp()
    {
        _validator = new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance);
    }

    [TestCase("SELECT * FROM orders")]
    [TestCase("SELECT id, name FROM customers WHERE active = true")]
    [TestCase("WITH recent AS (SELECT * FROM orders WHERE created_time > '2026-01-01') SELECT count(*) FROM recent")]
    public void Validate_SelectQueries_Pass(string sql)
    {
        _validator.Validate(sql, "PostgreSQL").Should().BeNull();
    }

    [Test]
    public void Validate_Explain_Passes()
    {
        _validator.Validate("EXPLAIN SELECT * FROM orders", "PostgreSQL").Should().BeNull();
    }

    [TestCase("INSERT INTO orders (id) VALUES (1)")]
    [TestCase("UPDATE orders SET status = 'X'")]
    [TestCase("DELETE FROM orders")]
    [TestCase("DROP TABLE orders")]
    [TestCase("TRUNCATE TABLE orders")]
    [TestCase("CREATE TABLE evil (id int)")]
    [TestCase("MERGE INTO target USING source ON target.id = source.id WHEN MATCHED THEN UPDATE SET v = source.v")]
    public void Validate_WriteStatements_AreRejected(string sql)
    {
        _validator.Validate(sql, "PostgreSQL").Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void Validate_StackedStatements_AreRejected()
    {
        var error = _validator.Validate("SELECT 1; DROP TABLE orders", "PostgreSQL");

        error.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void Validate_StackedSelects_AreRejected()
    {
        // Even read-only stacking is rejected — single statement only
        _validator.Validate("SELECT 1; SELECT 2", "PostgreSQL").Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void Validate_InsertFromSelect_IsRejected()
    {
        _validator.Validate("INSERT INTO archive SELECT * FROM orders", "PostgreSQL").Should().NotBeNullOrWhiteSpace();
    }

    [TestCase("MSSQL")]
    [TestCase("MySQL")]
    [TestCase("Snowflake")]
    [TestCase("AzureSynapse")]
    public void Validate_WriteStatement_RejectedAcrossDialects(string dialect)
    {
        _validator.Validate("DELETE FROM orders", dialect).Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void Validate_UnparseableSql_IsAllowedThrough()
    {
        // Regex guardrail stays authoritative; parser limitations must not block valid queries
        var error = _validator.Validate("SELECT listagg(x, ',') WITHIN GROUP (ORDER BY !!!) FROM", "Snowflake");

        error.Should().BeNull();
    }

    [Test]
    public void Validate_EmptySql_IsAllowedThrough()
    {
        _validator.Validate("", "PostgreSQL").Should().BeNull();
    }

    [Test]
    public void Validate_UnknownDialect_FallsBackToGeneric()
    {
        _validator.Validate("DELETE FROM orders", "SomethingElse").Should().NotBeNullOrWhiteSpace();
    }
}

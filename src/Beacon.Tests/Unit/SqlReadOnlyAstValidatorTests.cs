using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Beacon.Core.Services.Validation;

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

    [Test]
    public void Validate_ExplainAnalyzeSelect_Passes()
    {
        _validator.Validate("EXPLAIN ANALYZE SELECT * FROM orders", "PostgreSQL").Should().BeNull();
    }

    [TestCase("EXPLAIN ANALYZE INSERT INTO t VALUES (1)")]
    [TestCase("EXPLAIN UPDATE t SET a = 1")]
    [TestCase("EXPLAIN DELETE FROM t")]
    public void Validate_ExplainWrappedDml_IsRejected(string sql)
    {
        // On PostgreSQL/MySQL/Databricks, `EXPLAIN ANALYZE <DML>` actually EXECUTES the wrapped write.
        // The validator must recurse into the wrapped statement and reject it, not accept EXPLAIN
        // unconditionally (§1.5 read-only).
        _validator.Validate(sql, "PostgreSQL").Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void Validate_ExplainWrappingDataModifyingCte_IsRejected()
    {
        // Defense in depth: an EXPLAIN wrapping a SELECT whose CTE performs a write must also be
        // rejected. If the parser rejects this outright, the fail-closed parse path handles it — either
        // outcome is a rejection.
        _validator.Validate("EXPLAIN WITH x AS (INSERT INTO t VALUES (1) RETURNING id) SELECT * FROM x", "PostgreSQL")
            .Should().NotBeNullOrWhiteSpace();
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

    [TestCase("WITH x AS (INSERT INTO t VALUES(1) RETURNING id) SELECT * FROM x")]
    [TestCase("WITH x AS (DELETE FROM t RETURNING id) SELECT * FROM x")]
    [TestCase("WITH x AS (UPDATE t SET a = 1 RETURNING id) SELECT * FROM x")]
    public void Validate_DataModifyingCte_IsRejected(string sql)
    {
        // A data-modifying CTE parses as an outer Select but performs a real write — the INSERT
        // arm surfaces as SetExpression.Insert; the DELETE/UPDATE arms fail to parse and are
        // caught by the fail-closed parse path. All must be rejected (§1.5 read-only).
        _validator.Validate(sql, "PostgreSQL").Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void Validate_ReadOnlyCte_Passes()
    {
        _validator.Validate("WITH x AS (SELECT * FROM t) SELECT * FROM x", "PostgreSQL").Should().BeNull();
    }

    [Test]
    public void Validate_SelectIntoInsideUnionArm_IsRejected()
    {
        // SELECT ... INTO hidden in a UNION arm still creates a table — the set-operation walk
        // must catch it, not just the top-level SELECT.
        _validator.Validate("SELECT * FROM a UNION SELECT * INTO evil FROM b", "PostgreSQL").Should().NotBeNullOrWhiteSpace();
    }

    [TestCase("MSSQL")]
    [TestCase("MySQL")]
    [TestCase("Snowflake")]
    [TestCase("AzureSynapse")]
    public void Validate_WriteStatement_RejectedAcrossDialects(string dialect)
    {
        _validator.Validate("DELETE FROM orders", dialect).Should().NotBeNullOrWhiteSpace();
    }

    [TestCase("PostgreSQL")]
    [TestCase("MSSQL")]
    public void Validate_SelectInto_IsRejected(string dialect)
    {
        // SELECT ... INTO creates a table in both PostgreSQL and SQL Server — it parses as a
        // SELECT statement but is a write, so the AST validator must reject it.
        _validator.Validate("SELECT * INTO evil FROM orders", dialect).Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void Validate_UnparseableSql_IsRejected()
    {
        // Fail closed: this validator is the sole read-only gate at the query-builder and
        // connector call sites, so SQL the parser cannot handle must be rejected, not allowed.
        var error = _validator.Validate("SELECT listagg(x, ',') WITHIN GROUP (ORDER BY !!!) FROM", "Snowflake");

        error.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void Validate_EmptySql_IsAllowedThrough()
    {
        _validator.Validate("", "PostgreSQL").Should().BeNull();
    }

    [Test]
    public void Validate_SqliteDialect_SelectPasses()
    {
        // "SQLite" is a real DatabaseEngineType — it must resolve to a concrete SQLite dialect, not
        // silently fall through to GenericDialect.
        _validator.Validate("SELECT * FROM orders", "SQLite").Should().BeNull();
    }

    [Test]
    public void Validate_SqliteDialect_WriteRejected()
    {
        _validator.Validate("DELETE FROM orders", "SQLite").Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void Validate_UnknownDialect_FallsBackToGeneric()
    {
        _validator.Validate("DELETE FROM orders", "SomethingElse").Should().NotBeNullOrWhiteSpace();
    }
}

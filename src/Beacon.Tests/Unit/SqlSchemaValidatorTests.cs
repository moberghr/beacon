using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Services.Validation;

namespace Beacon.Tests.Unit;

[TestFixture]
public class SqlSchemaValidatorTests
{
    private SqlSchemaValidator _validator = null!;

    [SetUp]
    public void SetUp()
    {
        _validator = new SqlSchemaValidator();
    }

    private static Dictionary<string, HashSet<string>> Catalog() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["orders"] = new(StringComparer.OrdinalIgnoreCase) { "id", "customer_id", "total", "created_time" },
        ["customers"] = new(StringComparer.OrdinalIgnoreCase) { "id", "name", "email" }
    };

    [Test]
    public void Validate_KnownColumns_IsValid()
    {
        var result = _validator.Validate("SELECT id, total FROM orders", Catalog(), "PostgreSQL");

        result.IsValid.Should().BeTrue();
        result.Error.Should().BeNull();
        result.Checked.Should().BeTrue();
    }

    [Test]
    public void Validate_QualifiedKnownColumns_IsValid()
    {
        var sql = "SELECT o.id, c.name FROM orders o JOIN customers c ON c.id = o.customer_id";

        var result = _validator.Validate(sql, Catalog(), "PostgreSQL");

        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void Validate_HallucinatedColumnInWhere_IsRejected()
    {
        // The validator registers tables/aliases from FROM before evaluating WHERE/JOIN/ORDER BY,
        // so a hallucinated column there is caught.
        var result = _validator.Validate("SELECT id FROM orders WHERE nonexistent_col = 1", Catalog(), "PostgreSQL");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("nonexistent_col");
    }

    [Test]
    public void Validate_HallucinatedColumnOnAliasInJoin_IsRejected()
    {
        var sql = "SELECT c.name FROM customers c JOIN orders o ON o.bogus = c.id";

        var result = _validator.Validate(sql, Catalog(), "PostgreSQL");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("bogus");
    }

    [Test]
    public void Validate_UnknownTable_IsSkipped()
    {
        // Tables absent from the catalog are not validated (avoids false positives)
        var result = _validator.Validate("SELECT anything FROM not_in_catalog", Catalog(), "PostgreSQL");

        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void Validate_SyntaxError_IsReported()
    {
        var result = _validator.Validate("SELECT FROM WHERE", Catalog(), "PostgreSQL");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("syntax");
    }

    [Test]
    public void Validate_EmptyCatalog_IsNotChecked()
    {
        var result = _validator.Validate("SELECT whatever FROM anything", new Dictionary<string, HashSet<string>>(), "PostgreSQL");

        // An unknown schema cannot contradict the SQL, but the caller must be able to tell that the
        // verdict is "not checked" rather than "verified against the catalog".
        result.IsValid.Should().BeTrue();
        result.Checked.Should().BeFalse();
    }

    [Test]
    public void Validate_TablesUsed_ResolvesAliasesAndExcludesCtes()
    {
        var sql = "WITH r AS (SELECT * FROM public.orders o) SELECT c.name FROM r JOIN customers c ON c.id = r.customer_id";

        var result = _validator.Validate(sql, Catalog(), "PostgreSQL");

        result.IsValid.Should().BeTrue();
        result.TablesUsed.Should().Equal("public.orders", "customers");
    }

    [Test]
    public void Validate_ProjectionAliasInOrderBy_IsValid()
    {
        // `grand` exists on no table — it is a projection alias, so it must not be validated against
        // the catalog.
        var sql = "SELECT SUM(total) AS grand FROM orders GROUP BY customer_id ORDER BY grand";

        var result = _validator.Validate(sql, Catalog(), "PostgreSQL");

        result.IsValid.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    [Test]
    public void Validate_AzureSynapseDialect_Parses()
    {
        // AzureSynapse is T-SQL: TOP must parse, not fall through to GenericDialect and fail.
        var result = _validator.Validate("SELECT TOP 5 id FROM orders", Catalog(), "AzureSynapse");

        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void Validate_AmbiguousBareColumnAcrossTwoTables_IsSkipped()
    {
        // With more than one real table, bare (unqualified) columns are not validated
        var sql = "SELECT bogus FROM orders, customers";

        var result = _validator.Validate(sql, Catalog(), "PostgreSQL");

        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void Validate_CollectsReferencedColumns()
    {
        var sql = "SELECT o.id, c.name FROM orders o JOIN customers c ON c.id = o.customer_id WHERE o.total > 10";

        var result = _validator.Validate(sql, Catalog(), "PostgreSQL");

        result.IsValid.Should().BeTrue();
        result.ColumnsUsed.Should().Contain(["id", "name", "customer_id", "total"]);
    }

    [Test]
    public void Validate_InvalidColumn_StillCollectsReferencedColumns()
    {
        var result = _validator.Validate("SELECT bogus FROM orders", Catalog(), "PostgreSQL");

        result.IsValid.Should().BeFalse();
        result.ColumnsUsed.Should().Contain("bogus");
    }

    [Test]
    public void Validate_CteDeclaredButNotReferenced_BareColumnsStillChecked()
    {
        // A declared-but-unused CTE must not switch the bare-column check off for the real table. The CTE body
        // reads the same table so the single-real-table rule for bare columns still applies.
        var result = _validator.Validate("WITH r AS (SELECT id FROM orders) SELECT bogus FROM orders", Catalog(), "PostgreSQL");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("bogus");
    }

    [Test]
    public void Validate_CteWithAliasedColumn_NoFalsePositive()
    {
        // `x` exists only inside the CTE projection; validating it against `orders` would be a false positive.
        var result = _validator.Validate("WITH r AS (SELECT id AS x FROM orders) SELECT x FROM r", Catalog(), "PostgreSQL");

        result.IsValid.Should().BeTrue();
        result.TablesUsed.Should().Equal("orders");
    }

    [Test]
    public void Validate_CteInsideInSubquery_ExcludedFromTablesUsed()
    {
        var sql = "SELECT id FROM orders WHERE customer_id IN (WITH c AS (SELECT id FROM customers) SELECT id FROM c)";

        var result = _validator.Validate(sql, Catalog(), "PostgreSQL");

        result.TablesUsed.Should().Equal("orders", "customers");
        result.TablesUsed.Should().NotContain("c");
    }

    [Test]
    public void Validate_DerivedTableWithOwnCte_ExcludedFromTablesUsed()
    {
        var sql = "SELECT d.id FROM (WITH c AS (SELECT id FROM customers) SELECT id FROM c) d";

        var result = _validator.Validate(sql, Catalog(), "PostgreSQL");

        result.TablesUsed.Should().Equal("customers");
        result.IsValid.Should().BeTrue();
    }
}

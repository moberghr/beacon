using FluentAssertions;
using NUnit.Framework;
using Beacon.MCP.Services;

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
    public void Validate_EmptyCatalog_IsValid()
    {
        var result = _validator.Validate("SELECT whatever FROM anything", new Dictionary<string, HashSet<string>>(), "PostgreSQL");

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
}

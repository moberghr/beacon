using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services.Validation;

namespace Beacon.Tests.Unit;

/// <summary>
/// <see cref="SqlSemanticLinter"/> covers the three deterministic checks a schema-existence validator
/// cannot (spec item ⑥a, SC6): an undeclared join, an aggregate fanning out over a one-to-many join, and
/// a non-aggregated SELECT column missing from GROUP BY.
/// </summary>
[TestFixture]
public class SqlSemanticLinterTests
{
    private const string Dialect = "PostgreSQL";

    private readonly SqlSemanticLinter _linter = new();

    private static readonly SchemaLintContext EmptyContext = new(
        KnownJoins: [],
        PrimaryKeys: new Dictionary<string, IReadOnlySet<string>>(),
        Catalog: new Dictionary<string, HashSet<string>>());

    // orders (1) → order_items (many), order_items keyed by its own id: joining orders to order_items on
    // order_id fans an aggregate over orders out.
    private static SchemaLintContext FanoutContext()
    {
        return new SchemaLintContext(
            KnownJoins:
            [
                new SchemaJoinStep(
                    "sales.orders", "id", "sales.order_items", "order_id",
                    "FK", SchemaRelationshipOrigin.ForeignKey, IsVerified: true, Confidence: 1.0, ToIsJunction: false)
            ],
            PrimaryKeys: new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["sales.order_items"] = new HashSet<string> { "id" },
                ["order_items"] = new HashSet<string> { "id" }
            },
            Catalog: new Dictionary<string, HashSet<string>>());
    }

    [Test]
    public void Lint_JoinOnUndeclaredPair_FlagsUndeclaredJoin()
    {
        const string sql = "SELECT * FROM sales.orders o JOIN sales.customers c ON o.customer_id = c.id";

        var findings = _linter.Lint(sql, Dialect, EmptyContext);

        findings.Should().ContainSingle(x => x.Code == "UNDECLARED_JOIN");
        findings.Single().Message.Should().Contain("orders").And.Contain("customers");
    }

    [Test]
    public void Lint_WhereEqualityBetweenUndeclaredTables_FlagsUndeclaredJoin()
    {
        // TEST-4: comma join with the join condition in WHERE rather than an ON clause — the
        // equality-collection walk must find it there too, not just inside JOIN...ON.
        const string sql = "SELECT * FROM sales.orders o, sales.customers c WHERE o.customer_id = c.id";

        var findings = _linter.Lint(sql, Dialect, EmptyContext);

        findings.Should().ContainSingle(x => x.Code == "UNDECLARED_JOIN");
        findings.Single().Message.Should().Contain("orders").And.Contain("customers");
    }

    [Test]
    public void Lint_JoinOnDeclaredPair_DoesNotFlagUndeclaredJoin()
    {
        const string sql = "SELECT * FROM sales.orders o JOIN sales.customers c ON o.customer_id = c.id";

        var context = new SchemaLintContext(
            KnownJoins:
            [
                new SchemaJoinStep(
                    "sales.orders", "customer_id", "sales.customers", "id",
                    "FK", SchemaRelationshipOrigin.ForeignKey, IsVerified: true, Confidence: 1.0, ToIsJunction: false)
            ],
            PrimaryKeys: new Dictionary<string, IReadOnlySet<string>>(),
            Catalog: new Dictionary<string, HashSet<string>>());

        var findings = _linter.Lint(sql, Dialect, context);

        findings.Should().NotContain(x => x.Code == "UNDECLARED_JOIN");
    }

    [Test]
    public void Lint_AggregateOverTableJoinedOnNonPrimaryKey_FlagsFanoutAggregate()
    {
        const string sql = "SELECT SUM(o.amount) FROM sales.orders o JOIN sales.order_items i ON o.id = i.order_id";

        var context = new SchemaLintContext(
            KnownJoins:
            [
                new SchemaJoinStep(
                    "sales.orders", "id", "sales.order_items", "order_id",
                    "FK", SchemaRelationshipOrigin.ForeignKey, IsVerified: true, Confidence: 1.0, ToIsJunction: false)
            ],
            PrimaryKeys: new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["sales.order_items"] = new HashSet<string> { "id" },
                ["order_items"] = new HashSet<string> { "id" }
            },
            Catalog: new Dictionary<string, HashSet<string>>());

        var findings = _linter.Lint(sql, Dialect, context);

        findings.Should().ContainSingle(x => x.Code == "FANOUT_AGGREGATE");
        findings.Single().Message.Should().Contain("order_items").And.Contain("order_id");
    }

    [Test]
    public void Lint_AggregateOverTableJoinedOnFullPrimaryKey_DoesNotFlagFanoutAggregate()
    {
        const string sql = "SELECT SUM(o.amount) FROM sales.orders o JOIN sales.order_items i ON o.id = i.id";

        var context = new SchemaLintContext(
            KnownJoins:
            [
                new SchemaJoinStep(
                    "sales.orders", "id", "sales.order_items", "id",
                    "FK", SchemaRelationshipOrigin.ForeignKey, IsVerified: true, Confidence: 1.0, ToIsJunction: false)
            ],
            PrimaryKeys: new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["sales.order_items"] = new HashSet<string> { "id" },
                ["order_items"] = new HashSet<string> { "id" }
            },
            Catalog: new Dictionary<string, HashSet<string>>());

        var findings = _linter.Lint(sql, Dialect, context);

        findings.Should().NotContain(x => x.Code == "FANOUT_AGGREGATE");
    }

    [Test]
    public void Lint_CountDistinctOverTableJoinedOnNonPrimaryKey_DoesNotFlagFanoutAggregate()
    {
        // DISTINCT collapses the duplicate rows the one-to-many join introduces, so the aggregate cannot inflate.
        const string sql = "SELECT COUNT(DISTINCT o.id) FROM sales.orders o JOIN sales.order_items i ON o.id = i.order_id";

        var findings = _linter.Lint(sql, Dialect, FanoutContext());

        findings.Should().NotContain(x => x.Code == "FANOUT_AGGREGATE");
    }

    [Test]
    public void Lint_AggregateWrappedInScalarFunction_StillFlagsFanoutAggregate()
    {
        // COALESCE(SUM(...), 0) and ROUND(AVG(...), 2) are how models usually render aggregates — the
        // walk must look through the scalar wrapper or the check silently switches off.
        const string sql = "SELECT COALESCE(SUM(o.amount), 0) FROM sales.orders o JOIN sales.order_items i ON o.id = i.order_id";

        var findings = _linter.Lint(sql, Dialect, FanoutContext());

        findings.Should().ContainSingle(x => x.Code == "FANOUT_AGGREGATE");
    }

    [Test]
    public void Lint_AggregateWrappedInScalarFunction_StillFlagsGroupByMismatch()
    {
        const string sql = "SELECT o.customer_id, ROUND(AVG(o.amount), 2) AS avg_amount FROM sales.orders o";

        var findings = _linter.Lint(sql, Dialect, EmptyContext);

        findings.Should().ContainSingle(x => x.Code == "GROUP_BY_MISMATCH");
        findings.Single().Message.Should().Contain("customer_id");
    }

    [Test]
    public void Lint_AggregateInsideCaseBranch_StillFlagsGroupByMismatch()
    {
        const string sql = "SELECT o.customer_id, CASE WHEN SUM(o.amount) > 100 THEN 'big' ELSE 'small' END AS bucket FROM sales.orders o";

        var findings = _linter.Lint(sql, Dialect, EmptyContext);

        findings.Should().ContainSingle(x => x.Code == "GROUP_BY_MISMATCH");
    }

    [TestCase("SELECT c.id, c.name, COUNT(o.id) FROM sales.customers c JOIN sales.orders o ON o.customer_id = c.id GROUP BY c.id")]
    [TestCase("SELECT c.name, COUNT(o.id) FROM sales.customers c JOIN sales.orders o ON o.customer_id = c.id GROUP BY c.id")]
    [TestCase("SELECT c.id, c.name, COUNT(o.id) FROM sales.customers c JOIN sales.orders o ON o.customer_id = c.id GROUP BY 1")]
    public void Lint_NonAggregatedColumnFunctionallyDependentOnGroupedPrimaryKey_DoesNotFlagGroupByMismatch(string sql)
    {
        // Grouping by a table's full primary key determines every other column of that table — legal on
        // PostgreSQL/MySQL and never semantically wrong, so it must not burn a repair attempt.
        var context = new SchemaLintContext(
            KnownJoins: [],
            PrimaryKeys: new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["sales.customers"] = new HashSet<string> { "id" },
                ["customers"] = new HashSet<string> { "id" }
            },
            Catalog: new Dictionary<string, HashSet<string>>());

        var findings = _linter.Lint(sql, Dialect, context);

        findings.Should().NotContain(x => x.Code == "GROUP_BY_MISMATCH");
    }

    [Test]
    public void Lint_NonAggregatedColumnFromOtherTable_WhenOnlyOneTablesPrimaryKeyIsGrouped_StillFlagsGroupByMismatch()
    {
        // c.id covers c.name but says nothing about o.status — that one is still a real mismatch.
        const string sql = "SELECT c.name, o.status, COUNT(o.id) FROM sales.customers c JOIN sales.orders o ON o.customer_id = c.id GROUP BY c.id";

        var context = new SchemaLintContext(
            KnownJoins: [],
            PrimaryKeys: new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["sales.customers"] = new HashSet<string> { "id" },
                ["sales.orders"] = new HashSet<string> { "id" }
            },
            Catalog: new Dictionary<string, HashSet<string>>());

        var findings = _linter.Lint(sql, Dialect, context);

        findings.Should().ContainSingle(x => x.Code == "GROUP_BY_MISMATCH")
            .Which.Message.Should().Contain("status");
    }

    [Test]
    public void Lint_CompositePrimaryKeyOnlyPartiallyGrouped_StillFlagsGroupByMismatch()
    {
        const string sql = "SELECT i.order_id, i.line_no, i.sku, SUM(i.qty) FROM sales.order_items i GROUP BY i.order_id";

        var context = new SchemaLintContext(
            KnownJoins: [],
            PrimaryKeys: new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["sales.order_items"] = new HashSet<string> { "order_id", "line_no" }
            },
            Catalog: new Dictionary<string, HashSet<string>>());

        var findings = _linter.Lint(sql, Dialect, context);

        findings.Should().HaveCount(2).And.OnlyContain(x => x.Code == "GROUP_BY_MISMATCH");
    }

    [Test]
    public void Lint_NonAggregatedColumnMissingFromGroupBy_FlagsGroupByMismatch()
    {
        const string sql = "SELECT o.customer_id, SUM(o.amount) AS total FROM sales.orders o";

        var findings = _linter.Lint(sql, Dialect, EmptyContext);

        findings.Should().ContainSingle(x => x.Code == "GROUP_BY_MISMATCH");
        findings.Single().Message.Should().Contain("customer_id");
    }

    [Test]
    public void Lint_NonAggregatedColumnPresentInGroupBy_DoesNotFlagGroupByMismatch()
    {
        const string sql = "SELECT o.customer_id, SUM(o.amount) AS total FROM sales.orders o GROUP BY o.customer_id";

        var findings = _linter.Lint(sql, Dialect, EmptyContext);

        findings.Should().NotContain(x => x.Code == "GROUP_BY_MISMATCH");
    }

    [TestCase("PostgreSQL")]
    [TestCase("MySQL")]
    public void Lint_PositionalGroupBy_DoesNotFlagGroupByMismatch(string dialect)
    {
        // `GROUP BY 1` names the first projection item; it must count as present, not a mismatch.
        const string sql = "SELECT o.status, COUNT(*) AS c FROM sales.orders o GROUP BY 1";

        var findings = _linter.Lint(sql, dialect, EmptyContext);

        findings.Should().NotContain(x => x.Code == "GROUP_BY_MISMATCH");
    }

    [Test]
    public void Lint_PositionalGroupByCoveringOnlySomeColumns_FlagsTheUncoveredOne()
    {
        const string sql = "SELECT o.status, o.region, COUNT(*) AS c FROM sales.orders o GROUP BY 1";

        var findings = _linter.Lint(sql, Dialect, EmptyContext);

        findings.Should().ContainSingle(x => x.Code == "GROUP_BY_MISMATCH");
        findings.Single().Message.Should().Contain("region");
    }

    [Test]
    public void Lint_WindowFunction_IsNotTreatedAsAnAggregate()
    {
        // COUNT(*) OVER () never collapses rows, so plain columns beside it need no GROUP BY.
        const string sql = "SELECT o.id, o.status, COUNT(*) OVER () AS total FROM sales.orders o";

        var findings = _linter.Lint(sql, Dialect, EmptyContext);

        findings.Should().NotContain(x => x.Code == "GROUP_BY_MISMATCH");
    }

    // NF2: GROUP BY can reference the projection's OWN alias rather than the underlying column
    // (`o.customer_id AS cust ... GROUP BY cust`) — that must count as present, not a mismatch.
    [TestCase("PostgreSQL")]
    [TestCase("MySQL")]
    public void Lint_NonAggregatedColumnGroupedByItsOwnAlias_DoesNotFlagGroupByMismatch(string dialect)
    {
        const string sql = "SELECT o.customer_id AS cust, SUM(o.amount) AS total FROM sales.orders o GROUP BY cust";

        var findings = _linter.Lint(sql, dialect, EmptyContext);

        findings.Should().NotContain(x => x.Code == "GROUP_BY_MISMATCH");
    }

    [Test]
    public void Lint_CteAliasJoinedToRealTable_TreatsCteOpaqueAndFlagsNothing()
    {
        // If the CTE alias "r" were mistakenly treated as a real table, this would flag an
        // UNDECLARED_JOIN (no known join between it and sales.customers) AND a FANOUT_AGGREGATE
        // (the aggregate's table would be unresolved either way). Neither must fire: a CTE's
        // column set is unknown, so it is opaque to both checks — mirroring SqlSchemaValidator.
        const string sql = """
            WITH recent AS (SELECT * FROM sales.orders)
            SELECT SUM(r.amount) FROM recent r JOIN sales.customers c ON r.customer_id = c.id
            """;

        var findings = _linter.Lint(sql, Dialect, EmptyContext);

        findings.Should().BeEmpty();
    }

    [Test]
    public void Lint_DerivedTableAliasJoinedToRealTable_TreatsDerivedOpaqueAndFlagsNothing()
    {
        const string sql = "SELECT SUM(d.amount) FROM (SELECT * FROM sales.orders) d JOIN sales.customers c ON d.customer_id = c.id";

        var findings = _linter.Lint(sql, Dialect, EmptyContext);

        findings.Should().BeEmpty();
    }

    [Test]
    public void Lint_UnparseableSql_ReturnsEmptyFindings()
    {
        const string sql = "SELEC*T not valid &&& sql {{{";

        var findings = _linter.Lint(sql, Dialect, EmptyContext);

        findings.Should().BeEmpty();
    }

    [Test]
    public void Lint_BlankSql_ReturnsEmptyFindings()
    {
        var findings = _linter.Lint("   ", Dialect, EmptyContext);

        findings.Should().BeEmpty();
    }
}

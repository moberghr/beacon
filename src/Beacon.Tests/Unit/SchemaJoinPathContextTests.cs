using System.Text;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services.Metadata;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// Covers SC3/SC4/SC8 at the rendering boundary: the join-path and coverage blocks the prompt builder
/// emits from a <see cref="SchemaExpansion"/>. The expansion itself is covered by
/// <see cref="SchemaGraphTests"/>; this fixture asserts the text the model actually receives.
/// </summary>
[TestFixture]
public class SchemaJoinPathContextTests
{
    [Test]
    public void AppendJoinPaths_VerifiedTwoHopPath_RendersChainAndBothOnClauses()
    {
        var sb = new StringBuilder();
        var graph = OrderProductGraph();
        var expansion = graph.Expand(["sales.orders", "sales.products"], maxTables: 20);

        SchemaContextFormatter.AppendJoinPaths(sb, expansion.JoinPaths);
        var output = sb.ToString();

        output.Should().Contain("## Join Paths (verified)");
        output.Should().Contain("sales.orders → sales.order_products → sales.products");
        output.Should().Contain("sales.orders.id = sales.order_products.order_id");
        output.Should().Contain("sales.order_products.product_id = sales.products.id");
    }

    [Test]
    public void AppendJoinPaths_PathThroughJunction_LabelsTheLinkTable()
    {
        var sb = new StringBuilder();
        var expansion = OrderProductGraph().Expand(["sales.orders", "sales.products"], maxTables: 20);

        SchemaContextFormatter.AppendJoinPaths(sb, expansion.JoinPaths);

        sb.ToString().Should().Contain("[link table: sales.order_products]");
    }

    [Test]
    public void AppendJoinPaths_InferredPath_GoesUnderTheUnverifiedHeadingWithConfidence()
    {
        var sb = new StringBuilder();
        var tables = new List<TableMetadataDto>
        {
            Table("crm", "accounts", Pk("id")),
            Table("billing", "invoices", Pk("id"), Plain("account_id"))
        };
        var edges = new List<SchemaRelationshipEdge>
        {
            new("billing", "invoices", "account_id", "crm", "accounts", "id",
                "account", SchemaRelationshipOrigin.Inferred, IsVerified: false, Confidence: 0.9)
        };
        var expansion = SchemaGraph.Build(tables, edges).Expand(["billing.invoices", "crm.accounts"], maxTables: 20);

        SchemaContextFormatter.AppendJoinPaths(sb, expansion.JoinPaths);
        var output = sb.ToString();

        output.Should().Contain("UNVERIFIED");
        output.Should().Contain("confirm before relying on these");
        output.Should().Contain("(inferred, confidence 0.90)");
        output.Should().NotContain("## Join Paths (verified)");
    }

    [Test]
    public void AppendJoinPaths_MixedVerifiedAndInferred_KeepsThemInSeparateBlocks()
    {
        var sb = new StringBuilder();
        var tables = new List<TableMetadataDto>
        {
            Table("sales", "orders", Pk("id"), Plain("customer_id")),
            Table("sales", "customers", Pk("id")),
            Table("sales", "shipments", Pk("id"), Plain("order_id"))
        };
        var edges = new List<SchemaRelationshipEdge>
        {
            new("sales", "orders", "customer_id", "sales", "customers", "id",
                "customer", SchemaRelationshipOrigin.ForeignKey, IsVerified: true, Confidence: 1.0),
            new("sales", "shipments", "order_id", "sales", "orders", "id",
                "order", SchemaRelationshipOrigin.Inferred, IsVerified: false, Confidence: 0.9)
        };
        var graph = SchemaGraph.Build(tables, edges);
        var paths = new List<SchemaJoinPath>
        {
            graph.FindPath("sales.orders", "sales.customers")!,
            graph.FindPath("sales.shipments", "sales.orders")!
        };

        SchemaContextFormatter.AppendJoinPaths(sb, paths);
        var output = sb.ToString();

        output.Should().Contain("## Join Paths (verified)");
        output.Should().Contain("## Join Paths (UNVERIFIED");
        output.IndexOf("## Join Paths (verified)", StringComparison.Ordinal)
            .Should().BeLessThan(output.IndexOf("## Join Paths (UNVERIFIED", StringComparison.Ordinal),
                "verified joins are the ones the model should reach for first");
    }

    [Test]
    public void AppendJoinPaths_NoPaths_RendersNothing()
    {
        var sb = new StringBuilder();

        SchemaContextFormatter.AppendJoinPaths(sb, []);

        sb.ToString().Should().BeEmpty();
    }

    [Test]
    public void AppendCoverage_TruncatedSet_StatesTheOmittedCount()
    {
        var sb = new StringBuilder();

        SchemaContextFormatter.AppendCoverage(sb, capped: true, omittedTableCount: 7, detailedTableCount: 20);
        var output = sb.ToString();

        output.Should().Contain("## Coverage");
        output.Should().Contain("7 related table(s) omitted");
        output.Should().Contain("20");
    }

    [Test]
    public void AppendCoverage_NotTruncated_RendersNothing()
    {
        var sb = new StringBuilder();

        SchemaContextFormatter.AppendCoverage(sb, capped: false, omittedTableCount: 0, detailedTableCount: 8);

        sb.ToString().Should().BeEmpty("an untruncated set needs no caveat");
    }

    [Test]
    public void Expand_ManyToManyQuestion_PutsTheJunctionInTheDetailedSet()
    {
        // The regression this whole batch exists for: before the graph, a link table sat two hops from
        // either seed and never entered the prompt, so the model could not write the join.
        var expansion = OrderProductGraph().Expand(["sales.orders", "sales.products"], maxTables: 20);

        expansion.DetailedTables.Should().Contain("sales.order_products");
    }

    private static SchemaGraph OrderProductGraph()
    {
        var tables = new List<TableMetadataDto>
        {
            Table("sales", "orders", Pk("id")),
            Table("sales", "products", Pk("id")),
            Table("sales", "order_products", Pk("order_id"), Pk("product_id"))
        };
        var edges = new List<SchemaRelationshipEdge>
        {
            new("sales", "order_products", "order_id", "sales", "orders", "id",
                "order", SchemaRelationshipOrigin.ForeignKey, IsVerified: true, Confidence: 1.0),
            new("sales", "order_products", "product_id", "sales", "products", "id",
                "product", SchemaRelationshipOrigin.ForeignKey, IsVerified: true, Confidence: 1.0)
        };

        return SchemaGraph.Build(tables, edges);
    }

    private static TableMetadataDto Table(string schema, string name, params ColumnMetadataDto[] columns) =>
        new(schema, name, columns, [], null);

    private static ColumnMetadataDto Pk(string name) =>
        new(name, "integer", false, true, false, 1, null, null, null, null, null);

    private static ColumnMetadataDto Plain(string name) =>
        new(name, "integer", true, false, false, 2, null, null, null, null, null);
}

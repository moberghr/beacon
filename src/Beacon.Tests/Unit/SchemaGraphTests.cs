using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services.Metadata;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// Covers SC3 (junction classification), SC4 (bounded join-path finding), SC7 (connected components and
/// isolated tables) and SC8 (capped expansion) for the pure graph.
/// </summary>
[TestFixture]
public class SchemaGraphTests
{
    [Test]
    public void Build_LinkTableWithCompositeKeyOfForeignKeys_IsClassifiedAsJunction()
    {
        var graph = OrderProductGraph();

        graph.GetNode("sales.order_products")!.IsJunction.Should().BeTrue();
        graph.GetNode("sales.orders")!.IsJunction.Should().BeFalse();
        graph.GetNode("sales.products")!.IsJunction.Should().BeFalse();
    }

    [Test]
    public void Build_CompositeKeyTableWithANonForeignKeyColumn_IsNotAJunction()
    {
        // pgGraph's rule: every primary-key column must also reference something. A composite entity with
        // its own key part is an entity, not a bridge.
        var tables = new List<TableMetadataDto>
        {
            Table("sales", "orders", Pk("id")),
            Table("sales", "products", Pk("id")),
            Table("sales", "order_lines", Pk("order_id"), Pk("line_number"))
        };
        var edges = new List<SchemaRelationshipEdge>
        {
            Edge("sales", "order_lines", "order_id", "sales", "orders", "id"),
            Edge("sales", "order_lines", "line_number", "sales", "products", "id")
        };

        var graph = SchemaGraph.Build(tables, edges);

        graph.GetNode("sales.order_lines")!.IsJunction.Should().BeTrue();

        var withOwnKeyPart = SchemaGraph.Build(tables,
        [
            Edge("sales", "order_lines", "order_id", "sales", "orders", "id")
        ]);

        withOwnKeyPart.GetNode("sales.order_lines")!.IsJunction.Should().BeFalse(
            "line_number references nothing, so the table carries its own identity");
    }

    [Test]
    public void Build_SingleColumnPrimaryKey_IsNeverAJunction()
    {
        var graph = OrderProductGraph();

        graph.GetNode("sales.orders")!.IsJunction.Should().BeFalse();
    }

    [Test]
    public void Build_RelationshipToATableMissingFromMetadata_IsDropped()
    {
        var tables = new List<TableMetadataDto> { Table("sales", "orders", Pk("id")) };
        var edges = new List<SchemaRelationshipEdge>
        {
            Edge("sales", "orders", "customer_id", "sales", "customers", "id")
        };

        var graph = SchemaGraph.Build(tables, edges);

        graph.EdgeCount.Should().Be(0, "a dangling edge must not enter the graph");
        graph.IsolatedTables().Should().Contain("sales.orders");
    }

    [Test]
    public void FindPath_DirectRelationship_ReturnsOneStepWithBothJoinColumns()
    {
        var graph = ChainGraph();

        var path = graph.FindPath("sales.orders", "sales.customers");

        path.Should().NotBeNull();
        path!.Steps.Should().HaveCount(1);
        path.Steps[0].FromColumn.Should().Be("customer_id");
        path.Steps[0].ToColumn.Should().Be("id");
        path.Steps[0].ToQualifiedName.Should().Be("sales.customers");
    }

    [Test]
    public void FindPath_TwoHopChain_ReturnsBothStepsInOrder()
    {
        var graph = ChainGraph();

        var path = graph.FindPath("sales.order_lines", "sales.customers");

        path.Should().NotBeNull();
        path!.Steps.Should().HaveCount(2);
        path.Steps[0].ToQualifiedName.Should().Be("sales.orders");
        path.Steps[1].ToQualifiedName.Should().Be("sales.customers");
    }

    [Test]
    public void FindPath_TraversesInEitherDirection()
    {
        var graph = ChainGraph();

        // orders -> customers is the declared direction; the reverse must still resolve, because a join
        // reads both ways.
        var reverse = graph.FindPath("sales.customers", "sales.orders");

        reverse.Should().NotBeNull();
        reverse!.Steps.Should().HaveCount(1);
        reverse.Steps[0].FromColumn.Should().Be("id");
        reverse.Steps[0].ToColumn.Should().Be("customer_id");
    }

    [Test]
    public void FindPath_ManyToManyThroughJunction_ReturnsPathIncludingTheLinkTable()
    {
        var graph = OrderProductGraph();

        var path = graph.FindPath("sales.orders", "sales.products");

        path.Should().NotBeNull();
        path!.Steps.Should().HaveCount(2);
        path.IntermediateQualifiedNames.Should().ContainSingle().Which.Should().Be("sales.order_products");
    }

    [Test]
    public void FindPath_BeyondMaxDepth_ReturnsNull()
    {
        var graph = ChainGraph();

        graph.FindPath("sales.order_lines", "sales.customers", maxDepth: 1).Should().BeNull();
    }

    [Test]
    public void FindPath_DisconnectedTables_ReturnsNull()
    {
        var graph = TwoIslandGraph();

        graph.FindPath("sales.orders", "hr.employees").Should().BeNull();
    }

    [Test]
    public void FindPath_SameTable_ReturnsNull()
    {
        var graph = ChainGraph();

        graph.FindPath("sales.orders", "sales.orders").Should().BeNull();
    }

    [Test]
    public void FindPath_UnknownTable_ReturnsNull()
    {
        var graph = ChainGraph();

        graph.FindPath("sales.orders", "sales.nope").Should().BeNull();
    }

    [Test]
    public void FindPath_InferredRelationship_MarksThePathUnverified()
    {
        var tables = new List<TableMetadataDto>
        {
            Table("sales", "orders", Pk("id"), Plain("customer_id")),
            Table("sales", "customers", Pk("id"))
        };
        var edges = new List<SchemaRelationshipEdge>
        {
            Edge("sales", "orders", "customer_id", "sales", "customers", "id",
                origin: SchemaRelationshipOrigin.Inferred, isVerified: false, confidence: 0.9)
        };

        var path = SchemaGraph.Build(tables, edges).FindPath("sales.orders", "sales.customers");

        path!.IsFullyVerified.Should().BeFalse();
        path.MinimumConfidence.Should().Be(0.9);
    }

    [Test]
    public void Expand_ManyToManySeeds_PullsTheJunctionIntoTheDetailedSet()
    {
        var graph = OrderProductGraph();

        var expansion = graph.Expand(["sales.orders", "sales.products"], maxTables: 20);

        expansion.DetailedTables.Should().Contain("sales.order_products");
        expansion.JoinPaths.Should().ContainSingle();
    }

    [Test]
    public void Expand_JunctionOnAPathBypassesTheTableCap()
    {
        var graph = OrderProductGraph();

        // Cap of 2 is exactly the two seeds — the junction must still come in, because dropping it makes
        // the join path unusable.
        var expansion = graph.Expand(["sales.orders", "sales.products"], maxTables: 2);

        expansion.DetailedTables.Should().Contain("sales.order_products");
    }

    [Test]
    public void Expand_MoreNeighboursThanTheCap_ReportsCappedWithOmittedCount()
    {
        var graph = HubGraph(neighbourCount: 10);

        var expansion = graph.Expand(["sales.hub"], maxTables: 4);

        expansion.Capped.Should().BeTrue();
        expansion.OmittedTableCount.Should().Be(7, "1 seed + 3 admitted neighbours of 10 leaves 7 omitted");
        expansion.DetailedTables.Should().HaveCount(4);
    }

    [Test]
    public void Expand_EverythingFitsUnderTheCap_IsNotCapped()
    {
        var graph = ChainGraph();

        var expansion = graph.Expand(["sales.orders"], maxTables: 20);

        expansion.Capped.Should().BeFalse();
        expansion.OmittedTableCount.Should().Be(0);
    }

    [Test]
    public void Expand_UnknownSeed_IsIgnored()
    {
        var graph = ChainGraph();

        var expansion = graph.Expand(["sales.nope"], maxTables: 20);

        expansion.DetailedTables.Should().BeEmpty();
    }

    [Test]
    public void ConnectedComponents_TwoIslands_ReportsTwo()
    {
        var graph = TwoIslandGraph();

        var components = graph.ConnectedComponents();

        components.Should().HaveCount(2);
        components.Max(x => x.Count).Should().Be(2);
    }

    [Test]
    public void IsolatedTables_TableWithNoRelationship_IsReported()
    {
        var tables = new List<TableMetadataDto>
        {
            Table("sales", "orders", Pk("id"), Plain("customer_id")),
            Table("sales", "customers", Pk("id")),
            Table("sales", "audit_log", Pk("id"))
        };
        var edges = new List<SchemaRelationshipEdge>
        {
            Edge("sales", "orders", "customer_id", "sales", "customers", "id")
        };

        var graph = SchemaGraph.Build(tables, edges);

        graph.IsolatedTables().Should().BeEquivalentTo(["sales.audit_log"]);
        graph.ConnectedComponents().Should().HaveCount(2, "the orphan is its own component");
    }

    [Test]
    public void JunctionTables_ReturnsClassifiedLinkTables()
    {
        OrderProductGraph().JunctionTables().Should().BeEquivalentTo(["sales.order_products"]);
    }

    private static SchemaGraph ChainGraph()
    {
        var tables = new List<TableMetadataDto>
        {
            Table("sales", "order_lines", Pk("id"), Plain("order_id")),
            Table("sales", "orders", Pk("id"), Plain("customer_id")),
            Table("sales", "customers", Pk("id"))
        };
        var edges = new List<SchemaRelationshipEdge>
        {
            Edge("sales", "order_lines", "order_id", "sales", "orders", "id"),
            Edge("sales", "orders", "customer_id", "sales", "customers", "id")
        };

        return SchemaGraph.Build(tables, edges);
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
            Edge("sales", "order_products", "order_id", "sales", "orders", "id"),
            Edge("sales", "order_products", "product_id", "sales", "products", "id")
        };

        return SchemaGraph.Build(tables, edges);
    }

    private static SchemaGraph TwoIslandGraph()
    {
        var tables = new List<TableMetadataDto>
        {
            Table("sales", "orders", Pk("id"), Plain("customer_id")),
            Table("sales", "customers", Pk("id")),
            Table("hr", "employees", Pk("id"), Plain("department_id")),
            Table("hr", "departments", Pk("id"))
        };
        var edges = new List<SchemaRelationshipEdge>
        {
            Edge("sales", "orders", "customer_id", "sales", "customers", "id"),
            Edge("hr", "employees", "department_id", "hr", "departments", "id")
        };

        return SchemaGraph.Build(tables, edges);
    }

    private static SchemaGraph HubGraph(int neighbourCount)
    {
        var tables = new List<TableMetadataDto> { Table("sales", "hub", Pk("id")) };
        var edges = new List<SchemaRelationshipEdge>();

        for (var i = 0; i < neighbourCount; i++)
        {
            tables.Add(Table("sales", $"spoke{i}", Pk("id"), Plain("hub_id")));
            edges.Add(Edge("sales", $"spoke{i}", "hub_id", "sales", "hub", "id"));
        }

        return SchemaGraph.Build(tables, edges);
    }

    private static TableMetadataDto Table(string schema, string name, params ColumnMetadataDto[] columns) =>
        new(schema, name, columns, [], null);

    private static ColumnMetadataDto Pk(string name) =>
        new(name, "integer", false, true, false, 1, null, null, null, null, null);

    private static ColumnMetadataDto Plain(string name) =>
        new(name, "integer", true, false, false, 2, null, null, null, null, null);

    private static SchemaRelationshipEdge Edge(
        string sourceSchema, string sourceTable, string sourceColumn,
        string targetSchema, string targetTable, string targetColumn,
        SchemaRelationshipOrigin origin = SchemaRelationshipOrigin.ForeignKey,
        bool isVerified = true,
        double confidence = 1.0) =>
        new(sourceSchema, sourceTable, sourceColumn, targetSchema, targetTable, targetColumn,
            sourceColumn, origin, isVerified, confidence);
}

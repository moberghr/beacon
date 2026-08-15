using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services.Metadata;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// Covers SC5/SC6 for the pure discovery half of relationship sync: foreign-key derivation, the three
/// naming-convention inference rules, and the ambiguity-drop rule that keeps a wrong join from being
/// invented where none can be proven.
/// </summary>
[TestFixture]
public class SchemaRelationshipInferenceTests
{
    private static readonly HashSet<string> NothingCovered = new(StringComparer.OrdinalIgnoreCase);

    [Test]
    public void DeriveForeignKeyRelationships_QualifiedForeignKey_ProducesVerifiedEdge()
    {
        var tables = new List<TableMetadataDto>
        {
            Table("sales", "customers", Pk("id")),
            Table("sales", "orders", Pk("id"), Fk("customer_id", "customers", "id", "sales", "fk_orders_customer"))
        };

        var edges = SchemaRelationshipSyncService.DeriveForeignKeyRelationships(tables);

        edges.Should().HaveCount(1);
        edges[0].Origin.Should().Be(SchemaRelationshipOrigin.ForeignKey);
        edges[0].SourceTable.Should().Be("orders");
        edges[0].TargetSchema.Should().Be("sales");
        edges[0].TargetTable.Should().Be("customers");
        edges[0].TargetColumn.Should().Be("id");
        edges[0].Confidence.Should().Be(1.0);
        edges[0].ConstraintName.Should().Be("fk_orders_customer");
    }

    [Test]
    public void DeriveForeignKeyRelationships_TargetMissingFromMetadata_ProducesNoDanglingEdge()
    {
        var tables = new List<TableMetadataDto>
        {
            Table("sales", "orders", Pk("id"), Fk("customer_id", "customers", "id", "sales"))
        };

        SchemaRelationshipSyncService.DeriveForeignKeyRelationships(tables).Should().BeEmpty();
    }

    [Test]
    public void DeriveForeignKeyRelationships_CompositeKey_KeepsConstraintNameOnBothEdges()
    {
        var tables = new List<TableMetadataDto>
        {
            Table("sales", "orders", Pk("id")),
            Table("sales", "order_lines", Pk("id"),
                Fk("order_id", "orders", "id", "sales", "fk_line_order"),
                Fk("order_region", "orders", "region", "sales", "fk_line_order"))
        };

        var edges = SchemaRelationshipSyncService.DeriveForeignKeyRelationships(tables);

        edges.Should().HaveCount(2);
        edges.Should().OnlyContain(x => x.ConstraintName == "fk_line_order");
    }

    [Test]
    public void InferRelationships_EntityIdSuffix_MatchesPluralTableAtHighConfidence()
    {
        var tables = new List<TableMetadataDto>
        {
            Table("sales", "customers", Pk("id")),
            Table("sales", "orders", Pk("id"), Plain("customer_id"))
        };

        var edges = SchemaRelationshipSyncService.InferRelationships(tables, NothingCovered);

        edges.Should().ContainSingle(x => x.SourceColumn == "customer_id");
        var edge = edges.Single(x => x.SourceColumn == "customer_id");
        edge.TargetTable.Should().Be("customers");
        edge.TargetColumn.Should().Be("id");
        edge.Origin.Should().Be(SchemaRelationshipOrigin.Inferred);
        edge.Confidence.Should().Be(0.9);
        edge.Label.Should().Be("customer");
    }

    [Test]
    public void InferRelationships_CamelCaseIdSuffix_IsMatched()
    {
        var tables = new List<TableMetadataDto>
        {
            Table("sales", "Customer", Pk("Id")),
            Table("sales", "Order", Pk("Id"), Plain("customerId"))
        };

        var edges = SchemaRelationshipSyncService.InferRelationships(tables, NothingCovered);

        edges.Should().ContainSingle(x => x.SourceColumn == "customerId");
        edges.Single(x => x.SourceColumn == "customerId").TargetTable.Should().Be("Customer");
    }

    [Test]
    public void InferRelationships_TableQualifiedColumnName_MatchesAtMediumConfidence()
    {
        var tables = new List<TableMetadataDto>
        {
            Table("sales", "customer", Pk("code")),
            Table("sales", "invoice", Pk("id"), Plain("customer_code"))
        };

        var edges = SchemaRelationshipSyncService.InferRelationships(tables, NothingCovered);

        var edge = edges.Single(x => x.SourceColumn == "customer_code");
        edge.TargetTable.Should().Be("customer");
        edge.TargetColumn.Should().Be("code");
        edge.Confidence.Should().Be(0.75);
    }

    [Test]
    public void InferRelationships_SharedPrimaryKeyName_MatchesAtLowConfidence()
    {
        var tables = new List<TableMetadataDto>
        {
            Table("sales", "region", Pk("region_code")),
            Table("sales", "store", Pk("id"), Plain("region_code"))
        };

        var edges = SchemaRelationshipSyncService.InferRelationships(tables, NothingCovered);

        var edge = edges.Single(x => x.SourceColumn == "region_code");
        edge.TargetTable.Should().Be("region");
        edge.Confidence.Should().Be(0.6);
    }

    [Test]
    public void InferRelationships_TwoEquallyRankedTargetsInDifferentSchemas_DropsTheInference()
    {
        var tables = new List<TableMetadataDto>
        {
            Table("archive", "customers", Pk("id")),
            Table("crm", "customers", Pk("id")),
            Table("billing", "invoices", Pk("id"), Plain("customer_id"))
        };

        var edges = SchemaRelationshipSyncService.InferRelationships(tables, NothingCovered);

        edges.Should().NotContain(x => x.SourceColumn == "customer_id",
            "an ambiguous target must produce no edge rather than an arbitrary one");
    }

    [Test]
    public void InferRelationships_AmbiguousAcrossSchemasButOneIsLocal_PrefersTheSameSchema()
    {
        var tables = new List<TableMetadataDto>
        {
            Table("archive", "customers", Pk("id")),
            Table("billing", "customers", Pk("id")),
            Table("billing", "invoices", Pk("id"), Plain("customer_id"))
        };

        var edges = SchemaRelationshipSyncService.InferRelationships(tables, NothingCovered);

        edges.Single(x => x.SourceColumn == "customer_id").TargetSchema.Should().Be("billing");
    }

    [Test]
    public void InferRelationships_GenericIdColumnMatchingEveryTable_IsDropped()
    {
        var tables = new List<TableMetadataDto>
        {
            Table("sales", "customers", Pk("id")),
            Table("sales", "products", Pk("id")),
            Table("sales", "orders", Pk("order_key"), Plain("id"))
        };

        var edges = SchemaRelationshipSyncService.InferRelationships(tables, NothingCovered);

        edges.Should().NotContain(x => x.SourceColumn == "id",
            "a column named `id` matches every table's primary key and must not resolve to one of them");
    }

    [Test]
    public void InferRelationships_ColumnAlreadyHasDeclaredForeignKey_IsNotInferred()
    {
        var tables = new List<TableMetadataDto>
        {
            Table("sales", "customers", Pk("id")),
            Table("sales", "orders", Pk("id"), Fk("customer_id", "customers", "id", "sales"))
        };

        SchemaRelationshipSyncService.InferRelationships(tables, NothingCovered)
            .Should().BeEmpty("a declared foreign key is ground truth; inference only fills gaps");
    }

    [Test]
    public void InferRelationships_EdgeAlreadyCovered_IsNotProposedAgain()
    {
        var tables = new List<TableMetadataDto>
        {
            Table("sales", "customers", Pk("id")),
            Table("sales", "orders", Pk("id"), Plain("customer_id"))
        };
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sales|orders|customer_id|sales|customers|id"
        };

        SchemaRelationshipSyncService.InferRelationships(tables, covered).Should().BeEmpty();
    }

    [Test]
    public void InferRelationships_SelfReferencingColumn_IsNotInferred()
    {
        var tables = new List<TableMetadataDto>
        {
            Table("sales", "employee", Pk("id"), Plain("employee_id"))
        };

        SchemaRelationshipSyncService.InferRelationships(tables, NothingCovered)
            .Should().BeEmpty("a table must not be inferred to reference itself by naming alone");
    }

    [Test]
    public void InferRelationships_TargetWithCompositePrimaryKey_IsNotAnInferenceTarget()
    {
        var tables = new List<TableMetadataDto>
        {
            Table("sales", "customers", Pk("tenant_id"), Pk("id")),
            Table("sales", "orders", Pk("id"), Plain("customer_id"))
        };

        SchemaRelationshipSyncService.InferRelationships(tables, NothingCovered)
            .Should().BeEmpty("a single column cannot stand in for a composite key");
    }

    [TestCase("customer_id", "customer")]
    [TestCase("customer_fk", "customer")]
    [TestCase("customerId", "customer")]
    [TestCase("region", "region")]
    [TestCase("id", "id")]
    public void DeriveLabel_StripsKeySuffixes(string columnName, string expected)
    {
        SchemaRelationshipSyncService.DeriveLabel(columnName).Should().Be(expected);
    }

    [Test]
    public void DeriveForeignKeyRelationships_ColumnIsSolePrimaryKey_IsOneToOne()
    {
        var tables = new List<TableMetadataDto>
        {
            Table("sales", "customers", Pk("id")),
            new("sales", "customer_profile",
                [FkPrimaryKey("customer_id", "customers", "id", "sales")],
                [],
                null)
        };

        var edges = SchemaRelationshipSyncService.DeriveForeignKeyRelationships(tables);

        edges.Single().Cardinality.Should().Be(SchemaRelationshipCardinality.OneToOne);
    }

    [Test]
    public void DeriveForeignKeyRelationships_NonUniqueColumn_IsOneToMany()
    {
        var tables = new List<TableMetadataDto>
        {
            Table("sales", "customers", Pk("id")),
            Table("sales", "orders", Pk("id"), Fk("customer_id", "customers", "id", "sales"))
        };

        var edges = SchemaRelationshipSyncService.DeriveForeignKeyRelationships(tables);

        edges.Single().Cardinality.Should().Be(SchemaRelationshipCardinality.OneToMany);
    }

    private static TableMetadataDto Table(string schema, string name, params ColumnMetadataDto[] columns) =>
        new(schema, name, columns, [], null);

    private static ColumnMetadataDto Pk(string name) =>
        new(name, "integer", false, true, false, 1, null, null, null, null, null);

    private static ColumnMetadataDto Plain(string name) =>
        new(name, "integer", true, false, false, 2, null, null, null, null, null);

    private static ColumnMetadataDto Fk(
        string name, string fkTable, string fkColumn, string? fkSchema, string? constraintName = null) =>
        new(name, "integer", true, false, true, 2, fkTable, fkColumn, null, null, null, null, fkSchema, constraintName);

    private static ColumnMetadataDto FkPrimaryKey(string name, string fkTable, string fkColumn, string? fkSchema) =>
        new(name, "integer", false, true, true, 1, fkTable, fkColumn, null, null, null, null, fkSchema, null);
}

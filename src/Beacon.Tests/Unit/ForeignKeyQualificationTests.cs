using Beacon.Core.Models.Metadata;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// Covers SC1 (schema-qualified foreign-key resolution) and SC2 (composite foreign keys stay grouped).
/// Before qualification landed, the target schema was extracted and discarded, so consumers matched on
/// bare table name and a duplicate name across schemas resolved to whichever sorted first.
/// </summary>
[TestFixture]
public class ForeignKeyQualificationTests
{
    [Test]
    public void Resolve_DuplicateTableNameAcrossSchemas_PicksTheQualifiedTarget()
    {
        var tables = new List<TableMetadataDto>
        {
            Table("archive", "customer"),
            Table("sales", "customer")
        };
        var column = FkColumn("customer_id", "customer", "id", fkSchema: "sales");

        var resolved = ForeignKeyTargetResolver.Resolve(tables, sourceSchema: "billing", column);

        resolved.Should().NotBeNull();
        resolved!.SchemaName.Should().Be("sales");
        resolved.TableName.Should().Be("customer");
    }

    [Test]
    public void Resolve_DuplicateTableNameAcrossSchemas_DoesNotPickTheFirstSortedTable()
    {
        // "archive" sorts before "sales"; the pre-fix FirstOrDefault-on-name would have returned it.
        var tables = new List<TableMetadataDto>
        {
            Table("archive", "customer"),
            Table("sales", "customer")
        };
        var column = FkColumn("customer_id", "customer", "id", fkSchema: "sales");

        var resolved = ForeignKeyTargetResolver.Resolve(tables, sourceSchema: "billing", column);

        resolved!.SchemaName.Should().NotBe("archive");
    }

    [Test]
    public void Resolve_QualifiedTargetSchemaMissingFromMetadata_ReturnsNull()
    {
        var tables = new List<TableMetadataDto> { Table("archive", "customer") };
        var column = FkColumn("customer_id", "customer", "id", fkSchema: "sales");

        var resolved = ForeignKeyTargetResolver.Resolve(tables, sourceSchema: "billing", column);

        resolved.Should().BeNull("a qualified FK must not fall back to a same-named table in another schema");
    }

    [Test]
    public void Resolve_UnqualifiedAmbiguousName_PrefersTheSourceSchema()
    {
        var tables = new List<TableMetadataDto>
        {
            Table("archive", "customer"),
            Table("billing", "customer")
        };
        var column = FkColumn("customer_id", "customer", "id", fkSchema: null);

        var resolved = ForeignKeyTargetResolver.Resolve(tables, sourceSchema: "billing", column);

        resolved!.SchemaName.Should().Be("billing");
    }

    [Test]
    public void Resolve_UnqualifiedAmbiguousNameWithNoSameSchemaCandidate_ReturnsNullRatherThanGuessing()
    {
        var tables = new List<TableMetadataDto>
        {
            Table("archive", "customer"),
            Table("sales", "customer")
        };
        var column = FkColumn("customer_id", "customer", "id", fkSchema: null);

        var resolved = ForeignKeyTargetResolver.Resolve(tables, sourceSchema: "billing", column);

        resolved.Should().BeNull("an ambiguous unqualified target must not resolve to an arbitrary schema");
    }

    [Test]
    public void Resolve_UnqualifiedUniqueName_ResolvesAcrossSchemas()
    {
        var tables = new List<TableMetadataDto> { Table("sales", "customer") };
        var column = FkColumn("customer_id", "customer", "id", fkSchema: null);

        var resolved = ForeignKeyTargetResolver.Resolve(tables, sourceSchema: "billing", column);

        resolved!.SchemaName.Should().Be("sales");
    }

    [Test]
    public void Resolve_NonForeignKeyColumn_ReturnsNull()
    {
        var tables = new List<TableMetadataDto> { Table("sales", "customer") };
        var column = new ColumnMetadataDto("name", "text", true, false, false, 1, null, null, null, null, null);

        ForeignKeyTargetResolver.Resolve(tables, sourceSchema: "sales", column).Should().BeNull();
    }

    [Test]
    public void GroupForeignKeys_CompositeKey_YieldsOneConstraintWithBothColumnPairs()
    {
        var table = new TableMetadataDto(
            "sales",
            "order_line",
            [
                FkColumn("order_id", "orders", "id", "sales", "fk_order_line_order", ordinal: 1),
                FkColumn("order_region", "orders", "region", "sales", "fk_order_line_order", ordinal: 2)
            ],
            [],
            null);

        var constraints = ForeignKeyTargetResolver.GroupForeignKeys(table);

        constraints.Should().HaveCount(1);
        constraints[0].ConstraintName.Should().Be("fk_order_line_order");
        constraints[0].TargetSchema.Should().Be("sales");
        constraints[0].TargetTable.Should().Be("orders");
        constraints[0].ColumnPairs.Should().BeEquivalentTo(new[]
        {
            new ForeignKeyColumnPair("order_id", "id"),
            new ForeignKeyColumnPair("order_region", "region")
        }, options => options.WithStrictOrdering());
    }

    [Test]
    public void GroupForeignKeys_TwoSingleColumnKeys_YieldTwoConstraints()
    {
        var table = new TableMetadataDto(
            "sales",
            "orders",
            [
                FkColumn("customer_id", "customer", "id", "sales", "fk_orders_customer", ordinal: 1),
                FkColumn("region_id", "region", "id", "sales", "fk_orders_region", ordinal: 2)
            ],
            [],
            null);

        var constraints = ForeignKeyTargetResolver.GroupForeignKeys(table);

        constraints.Should().HaveCount(2);
        constraints.Should().OnlyContain(x => x.ColumnPairs.Count == 1);
    }

    [Test]
    public void GroupForeignKeys_ColumnsWithoutConstraintName_AreTreatedAsSeparateKeys()
    {
        // Legacy rows and connectors that do not expose the constraint name must not be collapsed into
        // one composite key just because they share a target table.
        var table = new TableMetadataDto(
            "sales",
            "orders",
            [
                FkColumn("ship_to_id", "address", "id", "sales", constraintName: null, ordinal: 1),
                FkColumn("bill_to_id", "address", "id", "sales", constraintName: null, ordinal: 2)
            ],
            [],
            null);

        var constraints = ForeignKeyTargetResolver.GroupForeignKeys(table);

        constraints.Should().HaveCount(2);
        constraints.Should().OnlyContain(x => x.ConstraintName == null);
    }

    [Test]
    public void GroupForeignKeys_TableWithNoForeignKeys_ReturnsEmpty()
    {
        var table = Table("sales", "customer");

        ForeignKeyTargetResolver.GroupForeignKeys(table).Should().BeEmpty();
    }

    private static TableMetadataDto Table(string schema, string name) =>
        new(schema, name, [new ColumnMetadataDto("id", "integer", false, true, false, 1, null, null, null, null, null)], [], null);

    private static ColumnMetadataDto FkColumn(
        string name,
        string fkTable,
        string fkColumn,
        string? fkSchema,
        string? constraintName = null,
        int ordinal = 1) =>
        new(name, "integer", true, false, true, ordinal, fkTable, fkColumn, null, null, null, null, fkSchema, constraintName);
}

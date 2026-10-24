using Beacon.Core.Models.Metadata;
using Beacon.Core.Services;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Metadata;

/// <summary>
/// A refresh stores no foreign-key target whose schema, table or column name carries a quote, bracket, semicolon or control
/// character: such a target leaves the column's list, and the single-target fields name the first one left, or none.
/// </summary>
[TestFixture]
public class UnsafeForeignKeyTargetTests
{
    [Test]
    public void ColumnWithOnlySafeTargetsIsKeptAsItIs()
    {
        var column = Column(new ColumnForeignKeyReference("fk_a", "app", "tenants", "id"), new ColumnForeignKeyReference("fk_b", "app", "x", "tenant_id"));

        var kept = DatabaseMetadataService.WithoutUnsafeForeignKeyTargets(column, out var skipped);

        kept.Should().BeSameAs(column);
        skipped.Should().BeEmpty();
    }

    [TestCase("app\"", "tenants", "id")]
    [TestCase("app", "ten]ants", "id")]
    [TestCase("app", "tenants", "id;")]
    public void UnsafeFirstTargetLeavesTheListAndTheNextOneFillsTheSingleTargetFields(string schema, string table, string targetColumn)
    {
        var column = Column(new ColumnForeignKeyReference("fk_a", schema, table, targetColumn), new ColumnForeignKeyReference("fk_b", "app", "x", "tenant_id"));

        var kept = DatabaseMetadataService.WithoutUnsafeForeignKeyTargets(column, out var skipped);

        skipped.Should().ContainSingle();
        kept.ForeignKeys.Should().Equal(new ColumnForeignKeyReference("fk_b", "app", "x", "tenant_id"));
        (kept.IsForeignKey, kept.ForeignKeySchema, kept.ForeignKeyTable, kept.ForeignKeyColumn, kept.ForeignKeyConstraintName)
            .Should().Be((true, "app", "x", "tenant_id", "fk_b"));
    }

    [Test]
    public void UnsafeOnlyTargetLeavesTheColumnWithoutAForeignKey()
    {
        var column = new ColumnMetadataDto("tenant_id", "integer", false, false, true, 1, "ten'ants", "id", null, null, null, null, "app", "fk_a");

        var kept = DatabaseMetadataService.WithoutUnsafeForeignKeyTargets(column, out var skipped);

        skipped.Should().ContainSingle();
        (kept.IsForeignKey, kept.ForeignKeySchema, kept.ForeignKeyTable, kept.ForeignKeyColumn, kept.ForeignKeyConstraintName, kept.ForeignKeys)
            .Should().Be((false, (string?)null, (string?)null, (string?)null, (string?)null, (IReadOnlyList<ColumnForeignKeyReference>?)null));
        ColumnForeignKeys.Of(kept).Should().BeEmpty();
    }

    private static ColumnMetadataDto Column(params ColumnForeignKeyReference[] foreignKeys)
    {
        var first = foreignKeys[0];
        return new ColumnMetadataDto("tenant_id", "integer", false, false, true, 1, first.TargetTable, first.TargetColumn, null, null, null, null, first.TargetSchema, first.ConstraintName)
        {
            ForeignKeys = foreignKeys
        };
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using NUnit.Framework;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.RowSecurity;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.RowSecurity;

/// <summary>
/// The row-security model as each provider's translation context builds it (read from its design-time model, which keeps
/// the collation and store defaults the runtime model drops): the length and required-ness of every text
/// column, the provider's own filter on the live-attribute key index, an ordinal collation of table names and values on
/// SQL Server (none on PostgreSQL, which compares them ordinally already), and the data sources' row-security version as a
/// required 64-bit integer with no store default.
/// </summary>
[TestFixture]
public class RowSecurityModelTests
{
    [TestCase(typeof(AttributeDefinition), nameof(AttributeDefinition.Key), 63, false)]
    [TestCase(typeof(AttributeDefinition), nameof(AttributeDefinition.DisplayName), 200, false)]
    [TestCase(typeof(AttributeDefinition), nameof(AttributeDefinition.Description), 1000, true)]
    [TestCase(typeof(UserAttributeValue), nameof(UserAttributeValue.Value), 256, false)]
    [TestCase(typeof(UserAttributeValue), nameof(UserAttributeValue.CreatedByUserId), 200, true)]
    [TestCase(typeof(RowPolicy), nameof(RowPolicy.SchemaName), 200, false)]
    [TestCase(typeof(RowPolicy), nameof(RowPolicy.TableName), 200, false)]
    [TestCase(typeof(RowPolicy), nameof(RowPolicy.Name), 200, false)]
    [TestCase(typeof(RowPolicy), nameof(RowPolicy.Description), 1000, true)]
    [TestCase(typeof(RowPolicyVersion), nameof(RowPolicyVersion.DefinitionJson), null, false)]
    [TestCase(typeof(RowPolicyVersion), nameof(RowPolicyVersion.ContentHash), 64, false)]
    [TestCase(typeof(RowPolicyVersion), nameof(RowPolicyVersion.CreatedByUserId), 200, true)]
    [TestCase(typeof(TableClassification), nameof(TableClassification.SchemaName), 200, false)]
    [TestCase(typeof(TableClassification), nameof(TableClassification.TableName), 200, false)]
    public void ATextColumn_HasItsLengthAndRequiredness_OnBothProviders(Type entity, string name, int? maxLength, bool nullable)
    {
        using var postgreSql = NpgsqlTestContext.Create();
        using var sqlServer = SqlServerTestContext.Create();

        foreach (var property in new[] { Property(postgreSql, entity, name), Property(sqlServer, entity, name) })
        {
            property.GetMaxLength().Should().Be(maxLength);
            property.IsNullable.Should().Be(nullable);
        }
    }

    [TestCase(false, "archived_time IS NULL", null)]
    [TestCase(true, "[ArchivedTime] IS NULL", "Latin1_General_100_BIN2")]
    public void TheTranslationContext_GetsItsProvidersKeyFilter_AndOrdinalCollation(bool sqlServer, string liveFilter, string? ordinalCollation)
    {
        using var context = Context(sqlServer);

        var key = Model(context).FindEntityType(typeof(AttributeDefinition))!
            .GetIndexes()
            .Single(x => x.Properties.Count == 1 && x.Properties[0].Name == nameof(AttributeDefinition.Key));

        key.IsUnique.Should().BeTrue();
        key.GetFilter().Should().Be(liveFilter);
        new[]
            {
                Property(context, typeof(UserAttributeValue), nameof(UserAttributeValue.Value)),
                Property(context, typeof(RowPolicy), nameof(RowPolicy.SchemaName)),
                Property(context, typeof(RowPolicy), nameof(RowPolicy.TableName)),
                Property(context, typeof(TableClassification), nameof(TableClassification.SchemaName)),
                Property(context, typeof(TableClassification), nameof(TableClassification.TableName))
            }
            .Select(x => x.GetCollation())
            .Should().AllBe(ordinalCollation);
        Property(context, typeof(AttributeDefinition), nameof(AttributeDefinition.Key)).GetCollation().Should().BeNull();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TheRowSecurityVersion_IsARequiredLong_WithNoStoreDefault(bool sqlServer)
    {
        using var context = Context(sqlServer);

        var version = Property(context, typeof(DataSource), nameof(DataSource.RowSecurityVersion));

        version.ClrType.Should().Be<long>();
        version.IsNullable.Should().BeFalse();
        version.GetDefaultValueSql().Should().BeNull();
        version.FindAnnotation(RelationalAnnotationNames.DefaultValue).Should().BeNull("every new row sets it; the migration gives existing rows 0");
    }

    private static IProperty Property(BeaconContext context, Type entity, string name)
    {
        return Model(context).FindEntityType(entity)!.FindProperty(name)!;
    }

    private static IModel Model(BeaconContext context)
    {
        return context.GetService<IDesignTimeModel>().Model;
    }

    private static BeaconContext Context(bool sqlServer)
    {
        return sqlServer ? SqlServerTestContext.Create() : NpgsqlTestContext.Create();
    }
}

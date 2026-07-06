using System.Text;
using FluentAssertions;
using NUnit.Framework;
using Beacon.AI.Services.Knowledge;

namespace Beacon.Tests.Unit;

[TestFixture]
public class SchemaContextFormatterTests
{
    [Test]
    public void AppendTableWithFullColumns_RendersColumnTuplesWithFlagsAndDescription()
    {
        var sb = new StringBuilder();
        var columns = new List<SchemaColumn>
        {
            new("id", "integer", true, false, null, null, "surrogate key"),
            new("status", "text", false, true, null, null, null)
        };

        SchemaContextFormatter.AppendTableWithFullColumns(sb, "public", "orders", "Order headers", columns, isApi: false);
        var output = sb.ToString();

        output.Should().Contain("### public.orders");
        output.Should().Contain("Order headers");
        output.Should().Contain("- (id: integer, PK, NOT NULL, surrogate key)");
        output.Should().Contain("- (status: text)");
    }

    [Test]
    public void AppendTableWithFullColumns_IncludesSampleValuesAsExamples()
    {
        var sb = new StringBuilder();
        var columns = new List<SchemaColumn>
        {
            new("status", "text", false, true, null, null, null, SampleValuesJson: "[\"A\",\"I\"]")
        };

        SchemaContextFormatter.AppendTableWithFullColumns(sb, "public", "orders", null, columns, isApi: false);

        sb.ToString().Should().Contain("Examples: [A, I]");
    }

    [Test]
    public void AppendTableWithFullColumns_InvalidSampleJson_RendersWithoutExamples()
    {
        var sb = new StringBuilder();
        var columns = new List<SchemaColumn>
        {
            new("status", "text", false, true, null, null, null, SampleValuesJson: "not-json")
        };

        SchemaContextFormatter.AppendTableWithFullColumns(sb, "public", "orders", null, columns, isApi: false);

        sb.ToString().Should().NotContain("Examples:");
    }

    [Test]
    public void AppendTableWithFullColumns_AppendsMaxLengthToTypeWithoutParens()
    {
        var sb = new StringBuilder();
        var columns = new List<SchemaColumn>
        {
            new("code", "varchar", false, true, null, null, null, MaxLength: 20),
            new("name", "varchar(50)", false, true, null, null, null, MaxLength: 50)
        };

        SchemaContextFormatter.AppendTableWithFullColumns(sb, "public", "orders", null, columns, isApi: false);
        var output = sb.ToString();

        output.Should().Contain("(code: varchar(20))");
        output.Should().Contain("(name: varchar(50))");
        output.Should().NotContain("varchar(50)(50)");
    }

    [Test]
    public void AppendTableWithFullColumns_RendersForeignKeySection()
    {
        var sb = new StringBuilder();
        var columns = new List<SchemaColumn>
        {
            new("customer_id", "integer", false, false, "customers", "id", null)
        };

        SchemaContextFormatter.AppendTableWithFullColumns(sb, "public", "orders", null, columns, isApi: false);
        var output = sb.ToString();

        output.Should().Contain("Foreign Keys:");
        output.Should().Contain("- customer_id → customers.id");
    }

    [Test]
    public void AppendTableWithFullColumns_NoForeignKeys_OmitsSection()
    {
        var sb = new StringBuilder();
        var columns = new List<SchemaColumn>
        {
            new("id", "integer", true, false, null, null, null)
        };

        SchemaContextFormatter.AppendTableWithFullColumns(sb, "public", "orders", null, columns, isApi: false);

        sb.ToString().Should().NotContain("Foreign Keys:");
    }

    [Test]
    public void AppendTableWithFullColumns_ApiSource_OmitsSchemaPrefix()
    {
        var sb = new StringBuilder();
        var columns = new List<SchemaColumn>
        {
            new("id", "integer", true, false, null, null, null)
        };

        SchemaContextFormatter.AppendTableWithFullColumns(sb, "api", "endpoints", null, columns, isApi: true);

        sb.ToString().Should().Contain("### endpoints");
        sb.ToString().Should().NotContain("api.endpoints");
    }

    [Test]
    public void AppendTableCompact_RendersPkAndAllColumnNames()
    {
        var sb = new StringBuilder();
        var columns = new List<SchemaColumn>
        {
            new("id", "integer", true, false, null, null, null),
            new("status", "text", false, true, null, null, null)
        };

        SchemaContextFormatter.AppendTableCompact(sb, "public", "orders", "Order headers", columns, isApi: false);
        var output = sb.ToString();

        output.Should().Contain("- public.orders (PK: id) -- Order headers");
        output.Should().Contain("Columns: id, status");
    }

    [Test]
    public void AppendTableCompact_NoPrimaryKey_SaysNoPk()
    {
        var sb = new StringBuilder();
        var columns = new List<SchemaColumn>
        {
            new("value", "text", false, true, null, null, null)
        };

        SchemaContextFormatter.AppendTableCompact(sb, "public", "log", null, columns, isApi: false);

        sb.ToString().Should().Contain("(no PK)");
    }
}

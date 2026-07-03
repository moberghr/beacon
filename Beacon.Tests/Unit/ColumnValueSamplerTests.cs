using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Security;

namespace Beacon.Tests.Unit;

[TestFixture]
public class ColumnValueSamplerTests
{
    [Test]
    public void BuildSampleQuery_SqlServer_UsesTopAndBrackets()
    {
        var sql = ColumnValueSampler.BuildSampleQuery(DatabaseEngineType.MSSQL, "dbo", "Orders");

        sql.Should().Be("SELECT TOP 5 * FROM [dbo].[Orders]");
    }

    [Test]
    public void BuildSampleQuery_AzureSynapse_UsesTopAndBrackets()
    {
        var sql = ColumnValueSampler.BuildSampleQuery(DatabaseEngineType.AzureSynapse, "sales", "Facts");

        sql.Should().Be("SELECT TOP 5 * FROM [sales].[Facts]");
    }

    [Test]
    public void BuildSampleQuery_MySql_UsesBackticksAndLimit()
    {
        var sql = ColumnValueSampler.BuildSampleQuery(DatabaseEngineType.MySQL, "shop", "orders");

        sql.Should().Be("SELECT * FROM `shop`.`orders` LIMIT 5");
    }

    [Test]
    public void BuildSampleQuery_PostgreSql_UsesDoubleQuotesAndLimit()
    {
        var sql = ColumnValueSampler.BuildSampleQuery(DatabaseEngineType.PostgreSQL, "public", "orders");

        sql.Should().Be("SELECT * FROM \"public\".\"orders\" LIMIT 5");
    }

    [Test]
    public void BuildSampleQuery_Snowflake_UsesDoubleQuotesAndLimit()
    {
        var sql = ColumnValueSampler.BuildSampleQuery(DatabaseEngineType.Snowflake, "PUBLIC", "ORDERS");

        sql.Should().Be("SELECT * FROM \"PUBLIC\".\"ORDERS\" LIMIT 5");
    }

    [Test]
    public void BuildSampleQuery_RejectsIdentifiersWithSpecialCharacters()
    {
        var act = () => ColumnValueSampler.BuildSampleQuery(DatabaseEngineType.MSSQL, "dbo", "Weird]Name");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*table*Weird]Name*");
    }

    [Test]
    public void FormatValue_TruncatesLongValuesToFiftyChars()
    {
        var longValue = new string('x', 120);

        var formatted = ColumnValueSampler.FormatValue(longValue);

        formatted.Should().HaveLength(50);
    }

    [Test]
    public void FormatValue_ByteArray_ReturnsNull()
    {
        var formatted = ColumnValueSampler.FormatValue(new byte[] { 1, 2, 3 });

        formatted.Should().BeNull();
    }

    [Test]
    public void FormatValue_DateTime_UsesRoundTripFormat()
    {
        var value = new DateTime(2026, 6, 9, 12, 30, 0, DateTimeKind.Utc);

        var formatted = ColumnValueSampler.FormatValue(value);

        formatted.Should().Be("2026-06-09T12:30:00.0000000Z");
    }

    [Test]
    public void FormatValue_WhitespaceOnly_ReturnsNull()
    {
        var formatted = ColumnValueSampler.FormatValue("   ");

        formatted.Should().BeNull();
    }

    [Test]
    public void ApplySamples_PiiColumn_GetsNoSampleValues()
    {
        var guardrail = new Mock<IQueryGuardrailService>();
        guardrail
            .Setup(x => x.IsPiiColumn("email", It.IsAny<IReadOnlyList<string>?>()))
            .Returns(true);
        guardrail
            .Setup(x => x.IsPiiColumn("status", It.IsAny<IReadOnlyList<string>?>()))
            .Returns(false);
        var sampler = CreateSampler(guardrail);
        var columns = new List<ColumnMetadataDto>
        {
            CreateColumn("email", "text"),
            CreateColumn("status", "text")
        };
        var samples = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["email"] = ["a@b.com"],
            ["status"] = ["A", "I"]
        };

        var result = sampler.ApplySamples(columns, samples, null);

        result.Single(x => x.ColumnName == "email").SampleValues.Should().BeNull();
        result.Single(x => x.ColumnName == "status").SampleValues.Should().BeEquivalentTo("A", "I");
    }

    [Test]
    public void ApplySamples_BinaryColumn_IsSkippedWithoutPiiCheck()
    {
        var guardrail = new Mock<IQueryGuardrailService>();
        var sampler = CreateSampler(guardrail);
        var columns = new List<ColumnMetadataDto>
        {
            CreateColumn("payload", "varbinary(max)")
        };
        var samples = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["payload"] = ["0x00"]
        };

        var result = sampler.ApplySamples(columns, samples, null);

        result.Single().SampleValues.Should().BeNull();
        guardrail.Verify(
            x => x.IsPiiColumn(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>()),
            Times.Never);
    }

    [Test]
    public void ApplySamples_ColumnWithoutSampledValues_StaysNull()
    {
        var guardrail = new Mock<IQueryGuardrailService>();
        var sampler = CreateSampler(guardrail);
        var columns = new List<ColumnMetadataDto>
        {
            CreateColumn("untouched", "int")
        };

        var result = sampler.ApplySamples(columns, new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase), null);

        result.Single().SampleValues.Should().BeNull();
    }

    [Test]
    public void ApplySamples_CustomPatterns_ArePassedToGuardrail()
    {
        var guardrail = new Mock<IQueryGuardrailService>();
        var sampler = CreateSampler(guardrail);
        var columns = new List<ColumnMetadataDto>
        {
            CreateColumn("internal_code", "text")
        };
        var samples = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["internal_code"] = ["X1"]
        };
        var customPatterns = new List<string> { "internal_.*" };

        sampler.ApplySamples(columns, samples, customPatterns);

        guardrail.Verify(x => x.IsPiiColumn("internal_code", customPatterns), Times.Once);
    }

    [Test]
    public void ApplySamples_PiiShapedValues_AreDropped()
    {
        var guardrail = new Mock<IQueryGuardrailService>();
        var sampler = CreateSampler(guardrail);
        var columns = new List<ColumnMetadataDto>
        {
            CreateColumn("notes", "text")
        };
        var samples = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["notes"] = ["call back tomorrow", "john.doe@example.com"]
        };

        var result = sampler.ApplySamples(columns, samples, null);

        result.Single().SampleValues.Should().BeNull();
    }

    [TestCase("john.doe@example.com", true)]
    [TestCase("123-45-6789", true)]
    [TestCase("4111 1111 1111 1111", true)]
    [TestCase("+385 91 123 4567", true)]
    [TestCase("active", false)]
    [TestCase("Order shipped", false)]
    public void ContainsPiiValue_DetectsValueShapedPii(string value, bool expected)
    {
        ColumnValueSampler.ContainsPiiValue([value], null).Should().Be(expected);
    }

    [Test]
    public void ContainsPiiValue_CustomPatternMatchesValue()
    {
        ColumnValueSampler.ContainsPiiValue(["EMP-00123"], ["EMP-\\d+"]).Should().BeTrue();
    }

    private static ColumnValueSampler CreateSampler(Mock<IQueryGuardrailService> guardrail)
    {
        return new ColumnValueSampler(guardrail.Object, NullLogger<ColumnValueSampler>.Instance);
    }

    private static ColumnMetadataDto CreateColumn(string name, string dataType)
    {
        return new ColumnMetadataDto(name, dataType, true, false, false, 1, null, null, null, null, null);
    }
}

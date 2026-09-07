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
    public void BuildDistinctProbeQuery_PostgreSql_UsesDoubleQuotesAndBoundedLimits()
    {
        var sql = ColumnValueSampler.BuildDistinctProbeQuery(DatabaseEngineType.PostgreSQL, "public", "orders", "status");

        sql.Should().Be(
            "SELECT DISTINCT \"status\" FROM (SELECT \"status\" FROM \"public\".\"orders\" WHERE \"status\" IS NOT NULL LIMIT 1000) x LIMIT 13");
    }

    [Test]
    public void BuildDistinctProbeQuery_MySql_UsesBackticksAndBoundedLimits()
    {
        var sql = ColumnValueSampler.BuildDistinctProbeQuery(DatabaseEngineType.MySQL, "shop", "orders", "status");

        // Default MySQL sql_mode reads "status" as a string literal, not an identifier — same backtick
        // rule as BuildSampleQuery_MySql_UsesBackticksAndLimit.
        sql.Should().Be(
            "SELECT DISTINCT `status` FROM (SELECT `status` FROM `shop`.`orders` WHERE `status` IS NOT NULL LIMIT 1000) x LIMIT 13");
    }

    [Test]
    public void BuildDistinctProbeQuery_SqlServer_UsesTopAndBrackets()
    {
        var sql = ColumnValueSampler.BuildDistinctProbeQuery(DatabaseEngineType.MSSQL, "dbo", "Orders", "Status");

        sql.Should().Be(
            "SELECT DISTINCT TOP 13 [Status] FROM (SELECT TOP 1000 [Status] FROM [dbo].[Orders] WHERE [Status] IS NOT NULL) x");
    }

    [Test]
    public void BuildDistinctProbeQuery_AzureSynapse_UsesTopAndBrackets()
    {
        var sql = ColumnValueSampler.BuildDistinctProbeQuery(DatabaseEngineType.AzureSynapse, "sales", "Facts", "Category");

        sql.Should().Be(
            "SELECT DISTINCT TOP 13 [Category] FROM (SELECT TOP 1000 [Category] FROM [sales].[Facts] WHERE [Category] IS NOT NULL) x");
    }

    [Test]
    public void BuildDistinctProbeQuery_RejectsIdentifiersWithSpecialCharacters()
    {
        var act = () => ColumnValueSampler.BuildDistinctProbeQuery(DatabaseEngineType.PostgreSQL, "public", "orders", "weird;col");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*column*weird;col*");
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
    public void ApplySamples_CompleteDomainColumn_SetsSampleValuesCompleteTrue()
    {
        var guardrail = new Mock<IQueryGuardrailService>();
        var sampler = CreateSampler(guardrail);
        var columns = new List<ColumnMetadataDto>
        {
            CreateColumn("status", "varchar")
        };
        var samples = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["status"] = ["A", "I", "P", "R", "X"]
        };
        var completeDomainColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "status" };

        var result = sampler.ApplySamples(columns, samples, null, completeDomainColumns);

        var status = result.Single();
        status.SampleValues.Should().BeEquivalentTo("A", "I", "P", "R", "X");
        status.SampleValuesComplete.Should().BeTrue();
    }

    [Test]
    public void ApplySamples_ColumnNotInCompleteSet_KeepsSampleValuesCompleteFalse()
    {
        var guardrail = new Mock<IQueryGuardrailService>();
        var sampler = CreateSampler(guardrail);
        var columns = new List<ColumnMetadataDto>
        {
            CreateColumn("notes", "text")
        };
        var samples = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["notes"] = ["hello", "world"]
        };
        var completeDomainColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "status" };

        var result = sampler.ApplySamples(columns, samples, null, completeDomainColumns);

        result.Single().SampleValuesComplete.Should().BeFalse();
    }

    [Test]
    public void ApplySamples_NoCompleteSetProvided_DefaultsToFalse()
    {
        var guardrail = new Mock<IQueryGuardrailService>();
        var sampler = CreateSampler(guardrail);
        var columns = new List<ColumnMetadataDto>
        {
            CreateColumn("status", "text")
        };
        var samples = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["status"] = ["A", "I"]
        };

        // Failure path: probe never ran / failed, so no complete-domain set is supplied — the
        // 5-row sample from SELECT * is kept as-is and marked incomplete.
        var result = sampler.ApplySamples(columns, samples, null);

        var status = result.Single();
        status.SampleValues.Should().BeEquivalentTo("A", "I");
        status.SampleValuesComplete.Should().BeFalse();
    }

    [Test]
    public void IsDomainCandidate_ShortStringColumn_IsCandidate()
    {
        var guardrail = new Mock<IQueryGuardrailService>();
        guardrail.Setup(x => x.IsPiiColumn(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>())).Returns(false);
        var sampler = CreateSampler(guardrail);
        var column = CreateColumn("status", "varchar", maxLength: 20);

        sampler.IsDomainCandidate(column, null).Should().BeTrue();
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public void IsDomainCandidate_PrimaryOrForeignKey_IsNotCandidate(bool isPrimaryKey, bool isForeignKey)
    {
        var guardrail = new Mock<IQueryGuardrailService>();
        var sampler = CreateSampler(guardrail);
        var column = new ColumnMetadataDto(
            "id", "varchar", true, isPrimaryKey, isForeignKey, 1, null, null, null, 20, null);

        sampler.IsDomainCandidate(column, null).Should().BeFalse();
    }

    [Test]
    public void IsDomainCandidate_MaxLengthOverHundred_IsNotCandidate()
    {
        var guardrail = new Mock<IQueryGuardrailService>();
        var sampler = CreateSampler(guardrail);
        var column = CreateColumn("description", "varchar", maxLength: 500);

        sampler.IsDomainCandidate(column, null).Should().BeFalse();
    }

    [Test]
    public void IsDomainCandidate_NonStringType_IsNotCandidate()
    {
        var guardrail = new Mock<IQueryGuardrailService>();
        var sampler = CreateSampler(guardrail);
        var column = CreateColumn("amount", "numeric", maxLength: null);

        sampler.IsDomainCandidate(column, null).Should().BeFalse();
    }

    [Test]
    public void IsDomainCandidate_PiiSkippedByName_IsNotCandidate()
    {
        var guardrail = new Mock<IQueryGuardrailService>();
        guardrail.Setup(x => x.IsPiiColumn("email", It.IsAny<IReadOnlyList<string>?>())).Returns(true);
        var sampler = CreateSampler(guardrail);
        var column = CreateColumn("email", "varchar", maxLength: 50);

        sampler.IsDomainCandidate(column, null).Should().BeFalse();
    }

    [Test]
    public void SelectDomainCandidates_CapsAtEightPerTable()
    {
        var guardrail = new Mock<IQueryGuardrailService>();
        guardrail.Setup(x => x.IsPiiColumn(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>())).Returns(false);
        var sampler = CreateSampler(guardrail);
        var columns = Enumerable.Range(1, 12)
            .Select(x => CreateColumn($"col{x}", "varchar", maxLength: 20))
            .ToList();

        var candidates = sampler.SelectDomainCandidates(columns, null);

        candidates.Should().HaveCount(8);
        candidates.Select(x => x.ColumnName).Should().BeEquivalentTo(
            columns.Take(8).Select(x => x.ColumnName), config => config.WithStrictOrdering());
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

    // TEST-3: the count→complete boundary the bounded DISTINCT domain probe relies on (spec item 4 —
    // the probe is capped one above MaxCompleteDomainValues so 13 rows back means "more values exist").
    [Test]
    public void IsCompleteDomain_TwelveValues_IsComplete()
    {
        var values = Enumerable.Range(1, 12).Select(x => $"v{x}").ToList();

        ColumnValueSampler.IsCompleteDomain(values).Should().BeTrue();
    }

    [Test]
    public void IsCompleteDomain_ThirteenValues_IsNotComplete()
    {
        var values = Enumerable.Range(1, 13).Select(x => $"v{x}").ToList();

        ColumnValueSampler.IsCompleteDomain(values).Should().BeFalse();
    }

    [Test]
    public void IsCompleteDomain_ZeroValues_IsNotComplete()
    {
        ColumnValueSampler.IsCompleteDomain([]).Should().BeFalse();
    }

    private static ColumnValueSampler CreateSampler(Mock<IQueryGuardrailService> guardrail)
    {
        return new ColumnValueSampler(guardrail.Object, NullLogger<ColumnValueSampler>.Instance);
    }

    private static ColumnMetadataDto CreateColumn(string name, string dataType, int? maxLength = null)
    {
        return new ColumnMetadataDto(name, dataType, true, false, false, 1, null, null, null, maxLength, null);
    }
}

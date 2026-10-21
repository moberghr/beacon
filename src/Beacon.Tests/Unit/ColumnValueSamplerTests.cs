using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

[TestFixture]
public class ColumnValueSamplerTests
{
    [Test]
    public void BuildSampleQuery_SqlServer_UsesTopAndBrackets()
    {
        var sql = ColumnValueSampler.BuildSampleQuery(DatabaseEngineType.MSSQL, "dbo", "Orders", ["Id", "Status"]);

        sql.Should().Be("SELECT TOP 5 [Id], [Status] FROM [dbo].[Orders]");
    }

    [Test]
    public void BuildSampleQuery_AzureSynapse_UsesTopAndBrackets()
    {
        var sql = ColumnValueSampler.BuildSampleQuery(DatabaseEngineType.AzureSynapse, "sales", "Facts", ["Category"]);

        sql.Should().Be("SELECT TOP 5 [Category] FROM [sales].[Facts]");
    }

    [Test]
    public void BuildSampleQuery_MySql_UsesBackticksAndLimit()
    {
        var sql = ColumnValueSampler.BuildSampleQuery(DatabaseEngineType.MySQL, "shop", "orders", ["id", "status"]);

        sql.Should().Be("SELECT `id`, `status` FROM `shop`.`orders` LIMIT 5");
    }

    [Test]
    public void BuildSampleQuery_PostgreSql_UsesDoubleQuotesAndLimit()
    {
        var sql = ColumnValueSampler.BuildSampleQuery(DatabaseEngineType.PostgreSQL, "public", "orders", ["id", "Status"]);

        sql.Should().Be("SELECT \"id\", \"Status\" FROM \"public\".\"orders\" LIMIT 5");
    }

    [Test]
    public void BuildSampleQuery_Snowflake_UsesDoubleQuotesAndLimit()
    {
        var sql = ColumnValueSampler.BuildSampleQuery(DatabaseEngineType.Snowflake, "PUBLIC", "ORDERS", ["STATUS"]);

        sql.Should().Be("SELECT \"STATUS\" FROM \"PUBLIC\".\"ORDERS\" LIMIT 5");
    }

    [Test]
    public void BuildSampleQuery_RejectsIdentifiersWithSpecialCharacters()
    {
        var act = () => ColumnValueSampler.BuildSampleQuery(DatabaseEngineType.MSSQL, "dbo", "Weird]Name", ["Id"]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*table*Weird]Name*");
    }

    [Test]
    public void BuildSampleQuery_RejectsAColumnNameWithSpecialCharacters()
    {
        var act = () => ColumnValueSampler.BuildSampleQuery(DatabaseEngineType.PostgreSQL, "public", "orders", ["status\";--"]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*column*");
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
    public void ContainsExcludedValue_DetectsValueShapedPii(string value, bool expected)
    {
        ColumnValueSampler.ContainsExcludedValue([value], null).Should().Be(expected);
    }

    [Test]
    public void ContainsExcludedValue_CustomPatternMatchesValue()
    {
        ColumnValueSampler.ContainsExcludedValue(["EMP-00123"], ["EMP-\\d+"]).Should().BeTrue();
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

    // A secret-like name (on ordinary data sources too, not only host ones) or a financial amount keeps no samples, and a
    // column that held some loses them: the refresh that stores the result clears them. An ordinary column keeps its own.
    [TestCase("api_key")]
    [TestCase("signing_key")]
    [TestCase("client_credential")]
    [TestCase("session_id")]
    [TestCase("pin_offset")]
    [TestCase("password_salt")]
    [TestCase("otp_seed")]
    [TestCase("current_balance")]
    [TestCase("gross_salary")]
    public void ApplySamples_ExcludedName_GetsNoSamples_AndLosesWhatItHeld(string name)
    {
        var sampler = CreateSampler(new Mock<IQueryGuardrailService>());
        var columns = new List<ColumnMetadataDto>
        {
            CreateColumn(name, "text") with { SampleValues = ["held-before"], SampleValuesComplete = true },
            CreateColumn("turnkey_id", "text"),
            CreateColumn("status", "text")
        };
        var samples = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [name] = ["abc"],
            ["turnkey_id"] = ["T-1"],
            ["status"] = ["A", "I"]
        };

        var result = sampler.ApplySamples(columns, samples, null);

        result.Single(x => x.ColumnName == name).SampleValues.Should().BeNull();
        result.Single(x => x.ColumnName == name).SampleValuesComplete.Should().BeFalse();
        result.Single(x => x.ColumnName == "turnkey_id").SampleValues.Should().Equal("T-1");
        result.Single(x => x.ColumnName == "status").SampleValues.Should().Equal("A", "I");
    }

    // One credential-shaped value drops the whole column's samples, whatever the column is called.
    [TestCase("ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8")]
    [TestCase("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxIn0")]
    [TestCase("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")]
    [TestCase("HR1210010051863000160")]
    public void ApplySamples_CredentialOrBankingShapedValue_DropsTheColumnsSamples(string value)
    {
        var sampler = CreateSampler(new Mock<IQueryGuardrailService>());
        var columns = new List<ColumnMetadataDto> { CreateColumn("reference", "text") };
        var samples = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["reference"] = ["ORD-1", value]
        };

        sampler.ApplySamples(columns, samples, null).Single().SampleValues.Should().BeNull();
    }

    // The complete-domain probe never reads an excluded column either.
    [Test]
    public void IsDomainCandidate_ExcludedName_IsNotCandidate()
    {
        var sampler = CreateSampler(new Mock<IQueryGuardrailService>());

        sampler.IsDomainCandidate(CreateColumn("api_key", "varchar", 40), null).Should().BeFalse();
        sampler.IsDomainCandidate(CreateColumn("balance_band", "varchar", 10), null).Should().BeFalse();
        sampler.IsDomainCandidate(CreateColumn("status", "varchar", 10), null).Should().BeTrue();
    }

    // Sampling runs on the provider's read-only path (a READ ONLY transaction where the engine has one), with the
    // identifiers quoted as the dialect quotes them; it never takes the plain execution path.
    [Test]
    public async Task EnrichWithSampleValues_RunsEveryStatementOnTheReadOnlyPath_WithQuotedIdentifiers()
    {
        var guardrail = new Mock<IQueryGuardrailService>();
        var provider = new Mock<IDataSourceProvider>(MockBehavior.Strict);
        var statements = new List<string>();
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .Callback<DataSource, string, Dictionary<string, object?>, CancellationToken>((_, sql, _, _) => statements.Add(sql))
            .ReturnsAsync(new ProviderQueryResult { Success = true, Rows = [new Dictionary<string, object?> { ["status"] = "PAID" }] });
        var sampler = CreateSampler(guardrail, provider.Object);
        var table = new TableMetadataDto("sales", "Orders", [CreateColumn("status", "varchar", 10)], [], null);

        var enriched = await sampler.EnrichWithSampleValuesAsync(Source(DatabaseEngineType.MSSQL), [table], null);

        statements.Should().Equal(
            "SELECT TOP 5 [status] FROM [sales].[Orders]",
            "SELECT DISTINCT TOP 13 [status] FROM (SELECT TOP 1000 [status] FROM [sales].[Orders] WHERE [status] IS NOT NULL) x");
        enriched.Single().Columns.Single().SampleValues.Should().Equal("PAID");
        enriched.Single().Columns.Single().SampleValuesComplete.Should().BeTrue();
    }

    // Every engine samples with its own quoting through the gate (on MySQL the schema is the configured database).
    [TestCase(
        DatabaseEngineType.PostgreSQL,
        "SELECT \"status\" FROM \"sales\".\"orders\" LIMIT 5",
        "SELECT DISTINCT \"status\" FROM (SELECT \"status\" FROM \"sales\".\"orders\" WHERE \"status\" IS NOT NULL LIMIT 1000) x LIMIT 13")]
    [TestCase(
        DatabaseEngineType.MySQL,
        "SELECT `status` FROM `sales`.`orders` LIMIT 5",
        "SELECT DISTINCT `status` FROM (SELECT `status` FROM `sales`.`orders` WHERE `status` IS NOT NULL LIMIT 1000) x LIMIT 13")]
    [TestCase(
        DatabaseEngineType.Snowflake,
        "SELECT \"status\" FROM \"sales\".\"orders\" LIMIT 5",
        "SELECT DISTINCT \"status\" FROM (SELECT \"status\" FROM \"sales\".\"orders\" WHERE \"status\" IS NOT NULL LIMIT 1000) x LIMIT 13")]
    public async Task EnrichWithSampleValues_OnEachEngine_RunsItsOwnQuotingOnTheReadOnlyPath(DatabaseEngineType engine, string sample, string probe)
    {
        var guardrail = new Mock<IQueryGuardrailService>();
        var provider = new Mock<IDataSourceProvider>(MockBehavior.Strict);
        var statements = new List<string>();
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .Callback<DataSource, string, Dictionary<string, object?>, CancellationToken>((_, sql, _, _) => statements.Add(sql))
            .ReturnsAsync(new ProviderQueryResult { Success = true, Rows = [new Dictionary<string, object?> { ["status"] = "PAID" }] });
        var sampler = CreateSampler(guardrail, provider.Object, configuredDatabases: TestConfiguredDatabases.Of("sales"));
        var table = new TableMetadataDto("sales", "orders", [CreateColumn("status", "varchar", 10)], [], null);

        var enriched = await sampler.EnrichWithSampleValuesAsync(Source(engine), [table], null);

        statements.Should().Equal(sample, probe);
        enriched.Single().Columns.Single().SampleValues.Should().Equal("PAID");
    }

    // A column whose name is left out, or a binary column, is never read: the row sample names only the others, and a
    // table with no other column is not queried at all.
    [Test]
    public async Task EnrichWithSampleValues_ReadsOnlyTheColumnsWhoseValuesMayDescribeThem()
    {
        var guardrail = new Mock<IQueryGuardrailService>();
        var provider = new Mock<IDataSourceProvider>(MockBehavior.Strict);
        var statements = new List<string>();
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .Callback<DataSource, string, Dictionary<string, object?>, CancellationToken>((_, sql, _, _) => statements.Add(sql))
            .ReturnsAsync(new ProviderQueryResult { Success = true, Rows = [] });
        var sampler = CreateSampler(guardrail, provider.Object);
        var orders = new TableMetadataDto(
            "public",
            "orders",
            [CreateColumn("id", "integer", null), CreateColumn("password_hash", "text", null), CreateColumn("salary", "numeric", null), CreateColumn("photo", "bytea", null), CreateColumn("quantity", "integer", null)],
            [],
            null);
        var secrets = new TableMetadataDto("public", "secrets", [CreateColumn("api_key", "text", null), CreateColumn("salary", "numeric", null)], [], null);

        await sampler.EnrichWithSampleValuesAsync(Source(DatabaseEngineType.PostgreSQL), [orders, secrets], null);

        statements.Should().Equal("SELECT \"id\", \"quantity\" FROM \"public\".\"orders\" LIMIT 5");
    }

    [Test]
    public async Task EnrichWithSampleValues_AFailedStatement_LeavesTheTableWithoutSamples()
    {
        var guardrail = new Mock<IQueryGuardrailService>();
        var provider = new Mock<IDataSourceProvider>(MockBehavior.Strict);
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult { Success = false, ErrorMessage = "permission denied for table orders" });
        var logs = new LogRecorder();
        var sampler = CreateSampler(guardrail, provider.Object, logs.For<ColumnValueSampler>());
        var table = new TableMetadataDto("public", "orders", [CreateColumn("status", "text")], [], null);
        var empty = new TableMetadataDto("public", "no_columns", [], [], null);

        var enriched = await sampler.EnrichWithSampleValuesAsync(Source(DatabaseEngineType.PostgreSQL), [table, empty], null);

        enriched[0].Should().BeSameAs(table);
        var skipped = logs.Entries.Should().ContainSingle(x => x.Level == Microsoft.Extensions.Logging.LogLevel.Warning).Subject;
        skipped.Message.Should().Contain("public.orders").And.Contain("failed on the data source");
        skipped.Message.Should().NotContain("permission denied", "the server's message can quote values (§1.11)");
        logs.Entries.Should().ContainSingle(x => x.Level == Microsoft.Extensions.Logging.LogLevel.Information)
            .Which.Message.Should().Contain("0 of 1 tables sampled");
    }

    // A value is screened whole as read, before it is cut to the sample length: a credential or an IBAN past the cut still
    // leaves the column out. Each refresh logs how many columns it left out, by reason — counts only.
    [Test]
    public async Task EnrichWithSampleValues_ScreensWholeValues_AndCountsTheColumnsLeftOutByReason()
    {
        var guardrail = new Mock<IQueryGuardrailService>();
        guardrail
            .Setup(x => x.IsPiiColumn(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>()))
            .Returns<string, IReadOnlyList<string>?>((name, _) => name == "email");
        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.Is<string>(y => !y.Contains("DISTINCT")), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult
            {
                Success = true,
                Rows =
                [
                    new Dictionary<string, object?>
                    {
                        ["note"] = $"A long free-text note about the order, then the key {TestCredentials.ProviderSecretKey}",
                        ["memo"] = "Refund approved by the branch office after review, paid to HR1210010051863000160",
                        ["email"] = "ivan@bank.example",
                        ["status"] = "PAID"
                    }
                ]
            });
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.Is<string>(y => y.Contains("DISTINCT")), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult { Success = true, Rows = [] });
        var logs = new LogRecorder();
        var sampler = CreateSampler(guardrail, provider.Object, logs.For<ColumnValueSampler>());
        var table = new TableMetadataDto(
            "public",
            "orders",
            [CreateColumn("note", "text"), CreateColumn("memo", "text"), CreateColumn("email", "text"), CreateColumn("status", "varchar", 10)],
            [],
            null);

        var enriched = await sampler.EnrichWithSampleValuesAsync(Source(DatabaseEngineType.PostgreSQL), [table], null);

        var columns = enriched.Single().Columns.ToDictionary(x => x.ColumnName);
        columns["note"].SampleValues.Should().BeNull("the credential sits past the sample length, yet the whole value was screened");
        columns["memo"].SampleValues.Should().BeNull("the IBAN sits past the sample length, yet the whole value was screened");
        columns["email"].SampleValues.Should().BeNull();
        columns["status"].SampleValues.Should().Equal("PAID");
        logs.Entries.Should().ContainSingle(x => x.Level == Microsoft.Extensions.Logging.LogLevel.Information)
            .Which.Message.Should().EndWith("columns left out by name 1, for a PII-shaped value 1, for a credential-shaped value 1");
        logs.Contains("sk_live").Should().BeFalse();
        logs.Contains("HR12").Should().BeFalse();
    }

    // Sampling statements pass the shared execution gate before the provider: on MySQL a schema is a database, so a table
    // in a database other than the source's configured one is never read.
    [Test]
    public async Task EnrichWithSampleValues_ATableTheGateRefuses_IsNotRead()
    {
        var guardrail = new Mock<IQueryGuardrailService>();
        var provider = new Mock<IDataSourceProvider>();
        var sampler = CreateSampler(guardrail, provider.Object);
        var table = new TableMetadataDto("hr", "salaries", [CreateColumn("grade", "varchar", 10)], [], null);

        var enriched = await sampler.EnrichWithSampleValuesAsync(Source(DatabaseEngineType.MySQL), [table], null);

        enriched.Single().Should().BeSameAs(table);
        provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static DataSource Source(DatabaseEngineType engine)
    {
        return new DataSource
        {
            Id = 4,
            Name = "warehouse",
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = engine,
            EncryptedConnectionData = "encrypted"
        };
    }

    private static ColumnValueSampler CreateSampler(
        Mock<IQueryGuardrailService> guardrail,
        IDataSourceProvider? provider = null,
        Microsoft.Extensions.Logging.ILogger<ColumnValueSampler>? logger = null,
        Beacon.Core.Services.Validation.IConfiguredDatabaseResolver? configuredDatabases = null)
    {
        var providers = new Mock<IDataSourceProviderFactory>();
        providers
            .Setup(x => x.GetProvider(It.IsAny<DataSourceType>()))
            .Returns(provider ?? Mock.Of<IDataSourceProvider>(MockBehavior.Strict));

        return new ColumnValueSampler(
            guardrail.Object,
            new GatedSqlRunner(TestSqlGate.Create(), providers.Object, configuredDatabases ?? TestConfiguredDatabases.None, TestQueryServices.QueryOptions),
            TestRowSecurity.Resolver,
            logger ?? NullLogger<ColumnValueSampler>.Instance);
    }

    private static ColumnMetadataDto CreateColumn(string name, string dataType, int? maxLength = null)
    {
        return new ColumnMetadataDto(name, dataType, true, false, false, 1, null, null, null, maxLength, null);
    }
}

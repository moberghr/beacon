using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Ask-time value grounding (spec item 5). Extraction/sanitising/ranking are pure and exercised
/// directly; the probe path is exercised end-to-end against a mocked <see cref="BeaconContext"/>
/// (async-queryable doubles, no DB — §4.7) and a mocked <see cref="IDataSourceProvider"/>, with the
/// REAL <see cref="SqlReadOnlyAstValidator"/> so a rejected probe proves the read-only gate actually
/// runs (§1.5) rather than merely being wired in.
/// </summary>
[TestFixture]
public class ValueGroundingServiceTests
{
    private const int DataSourceId = 1;

    // ---------- ExtractLiterals ----------

    [Test]
    public void ExtractLiterals_QuotedSpan_Extracted()
    {
        var literals = ValueGroundingService.ExtractLiterals("orders that were 'refunded' last week");

        literals.Should().ContainSingle().Which.Should().Be("refunded");
    }

    [Test]
    public void ExtractLiterals_CapitalisedMultiWordRun_ExtractedAsSinglePhrase()
    {
        var literals = ValueGroundingService.ExtractLiterals("show revenue for stores in New York please");

        literals.Should().Contain("New York");
    }

    [Test]
    public void ExtractLiterals_CodeToken_ExtractedRegardlessOfPosition()
    {
        var literals = ValueGroundingService.ExtractLiterals("find SKU123 and ACME-42 in the catalog");

        literals.Should().Contain("SKU123");
        literals.Should().Contain("ACME-42");
    }

    [Test]
    public void ExtractLiterals_StopListedCapitalisedWord_Excluded()
    {
        var literals = ValueGroundingService.ExtractLiterals("revenue by Top region this year");

        literals.Should().NotContain("Top");
    }

    [Test]
    public void ExtractLiterals_SentenceInitialCapitalisedWord_Excluded()
    {
        // "Refunded" is capitalised and not stop-listed, but it starts the sentence — excluded purely
        // by position (distinct from the stop-list rule above).
        var literals = ValueGroundingService.ExtractLiterals("Refunded orders in the last week");

        literals.Should().NotContain("Refunded");
    }

    [Test]
    public void ExtractLiterals_NumbersAndDates_Excluded()
    {
        var literals = ValueGroundingService.ExtractLiterals("orders after 2024-01-01 totaling 500 in count");

        literals.Should().NotContain("2024-01-01");
        literals.Should().NotContain("500");
    }

    [Test]
    public void ExtractLiterals_DisallowedCharset_DroppedNotEscaped()
    {
        var literals = ValueGroundingService.ExtractLiterals("find records for 'abc@def'");

        literals.Should().NotContain(x => x.Contains('@'), "a literal with a disallowed character is dropped, never escaped");
    }

    [Test]
    public void ExtractLiterals_CapsAtFourLiterals()
    {
        var literals = ValueGroundingService.ExtractLiterals(
            "'Alpha' 'Bravo' 'Charlie' 'Delta' 'Echo' 'Foxtrot'");

        literals.Should().HaveCount(4);
    }

    [Test]
    public void ExtractLiterals_BlankQuestion_ReturnsEmpty()
    {
        ValueGroundingService.ExtractLiterals("   ").Should().BeEmpty();
    }

    // ---------- SanitizeLiteral ----------

    [Test]
    public void SanitizeLiteral_DoublesQuotesAndStripsWildcards()
    {
        var sanitized = ValueGroundingService.SanitizeLiteral("O'Brien%_\\[x]");

        sanitized.Should().Be("o''brienx]");
    }

    [Test]
    public void SanitizeLiteral_LowerCasesTheLiteral()
    {
        ValueGroundingService.SanitizeLiteral("REFUNDED").Should().Be("refunded");
    }

    // ---------- BuildProbeSql ----------

    [Test]
    public void BuildProbeSql_SqlServer_UsesTopAndBrackets()
    {
        var sql = ValueGroundingService.BuildProbeSql(DatabaseEngineType.MSSQL, "dbo", "Orders", "Status", "refunded");

        sql.Should().Be("SELECT DISTINCT TOP 3 [Status] FROM [dbo].[Orders] WHERE LOWER([Status]) LIKE '%refunded%'");
    }

    [Test]
    public void BuildProbeSql_AzureSynapse_UsesTopAndBrackets()
    {
        var sql = ValueGroundingService.BuildProbeSql(DatabaseEngineType.AzureSynapse, "sales", "Facts", "Country", "germany");

        sql.Should().Be("SELECT DISTINCT TOP 3 [Country] FROM [sales].[Facts] WHERE LOWER([Country]) LIKE '%germany%'");
    }

    [Test]
    public void BuildProbeSql_PostgreSql_UsesDoubleQuotesAndLimit()
    {
        var sql = ValueGroundingService.BuildProbeSql(DatabaseEngineType.PostgreSQL, "public", "orders", "status", "refunded");

        sql.Should().Be("SELECT DISTINCT \"status\" FROM \"public\".\"orders\" WHERE LOWER(\"status\") LIKE '%refunded%' LIMIT 3");
    }

    [Test]
    public void BuildProbeSql_MySql_UsesDoubleQuotesAndLimit()
    {
        var sql = ValueGroundingService.BuildProbeSql(DatabaseEngineType.MySQL, "shop", "orders", "status", "refunded");

        sql.Should().Be("SELECT DISTINCT \"status\" FROM \"shop\".\"orders\" WHERE LOWER(\"status\") LIKE '%refunded%' LIMIT 3");
    }

    [Test]
    public void BuildProbeSql_SanitisesTheLiteralInsideThePattern()
    {
        var sql = ValueGroundingService.BuildProbeSql(DatabaseEngineType.PostgreSQL, "public", "orders", "status", "O'Brien%_");

        sql.Should().Contain("'%o''brien%'");
    }

    [Test]
    public void BuildProbeSql_RejectsIdentifiersWithSpecialCharacters()
    {
        var act = () => ValueGroundingService.BuildProbeSql(DatabaseEngineType.PostgreSQL, "public", "orders; DROP TABLE x", "status", "x");

        act.Should().Throw<InvalidOperationException>().WithMessage("*table*");
    }

    // ---------- RankCandidateColumns ----------

    [Test]
    public void RankCandidateColumns_SampleValueHit_RankedFirst()
    {
        var tables = new[]
        {
            new ValueGroundingTable("sales", "orders", new[]
            {
                new ValueGroundingColumn("notes", "text", false, null, null),
                new ValueGroundingColumn("status", "varchar", false, null, "[\"REFUNDED\",\"PAID\"]")
            })
        };

        var ranked = ValueGroundingService.RankCandidateColumns(tables, "refunded");

        ranked.First().Column.ColumnName.Should().Be("status");
    }

    [Test]
    public void RankCandidateColumns_PreferredColumnName_RankedAheadOfOthers()
    {
        var tables = new[]
        {
            new ValueGroundingTable("sales", "orders", new[]
            {
                new ValueGroundingColumn("comment", "text", false, null, null),
                new ValueGroundingColumn("country", "varchar", false, null, null)
            })
        };

        var ranked = ValueGroundingService.RankCandidateColumns(tables, "germany");

        ranked.First().Column.ColumnName.Should().Be("country");
    }

    [Test]
    public void RankCandidateColumns_ExcludesPrimaryKeyAndNonStringColumns()
    {
        var tables = new[]
        {
            new ValueGroundingTable("sales", "orders", new[]
            {
                new ValueGroundingColumn("id", "varchar", true, null, null),
                new ValueGroundingColumn("total", "numeric", false, null, null),
                new ValueGroundingColumn("status", "varchar", false, null, null)
            })
        };

        var ranked = ValueGroundingService.RankCandidateColumns(tables, "refunded");

        ranked.Should().ContainSingle().Which.Column.ColumnName.Should().Be("status");
    }

    // ---------- BuildValueMatchesBlockAsync (end to end) ----------

    [Test]
    public async Task BuildValueMatchesBlockAsync_SampleValueHit_ResolvesWithoutProbing()
    {
        var tables = new[]
        {
            new ValueGroundingTable("sales", "orders", new[]
            {
                new ValueGroundingColumn("status", "varchar", false, null, "[\"REFUNDED\",\"PAID\"]")
            })
        };

        var provider = new Mock<IDataSourceProvider>();
        var (service, _) = BuildService(provider);

        var block = await service.BuildValueMatchesBlockAsync(
            DataSourceId, "orders that were 'refunded'", tables, NewSettings(), CancellationToken.None);

        block.Should().Contain("sales.orders.status = 'REFUNDED'");
        provider.Verify(
            x => x.ExecuteQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task BuildValueMatchesBlockAsync_NoSampleHit_ProbesAndPassesTheReadOnlyGate()
    {
        var tables = new[]
        {
            new ValueGroundingTable("sales", "orders", new[]
            {
                new ValueGroundingColumn("status", "varchar", false, null, null)
            })
        };

        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult
            {
                Success = true,
                Rows = [new Dictionary<string, object?> { ["status"] = "REFUNDED" }]
            });
        var (service, _) = BuildService(provider);

        var block = await service.BuildValueMatchesBlockAsync(
            DataSourceId, "orders that were 'refunded'", tables, NewSettings(), CancellationToken.None);

        // Reaching the provider at all proves the constructed SELECT passed the REAL AST read-only gate.
        block.Should().Contain("sales.orders.status = 'REFUNDED'");
        provider.Verify(
            x => x.ExecuteQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // TEST-6: a probe hit that is PII-shaped (an email address) must never reach the rendered block —
    // PiiValueScreen.ContainsPiiValue is applied to probe results the same way it is to sample values.
    [Test]
    public async Task BuildValueMatchesBlockAsync_ProbeReturnsPiiShapedValue_ExcludedFromBlock()
    {
        var tables = new[]
        {
            new ValueGroundingTable("sales", "orders", new[]
            {
                new ValueGroundingColumn("contact_email", "varchar", false, null, null)
            })
        };

        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult
            {
                Success = true,
                Rows = [new Dictionary<string, object?> { ["contact_email"] = "john.doe@example.com" }]
            });
        var (service, _) = BuildService(provider);

        var block = await service.BuildValueMatchesBlockAsync(
            DataSourceId, "orders that were 'refunded'", tables, NewSettings(), CancellationToken.None);

        block.Should().Be("");
    }

    [Test]
    public async Task BuildValueMatchesBlockAsync_CapHonoured_StopsProbingAtTheConfiguredMax()
    {
        var tables = new[]
        {
            new ValueGroundingTable("sales", "orders", new[]
            {
                new ValueGroundingColumn("status", "varchar", false, null, null)
            }),
            new ValueGroundingTable("crm", "accounts", new[]
            {
                new ValueGroundingColumn("country", "varchar", false, null, null)
            })
        };

        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult { Success = true, Rows = [new Dictionary<string, object?> { ["v"] = "X" }] });
        var (service, _) = BuildService(provider);

        var settings = NewSettings();
        settings.ValueGroundingMaxProbes = 1;

        // Two literals, neither resolvable from a sample, each needing its own column probe.
        await service.BuildValueMatchesBlockAsync(
            DataSourceId, "orders that were 'refunded' in Germany", tables, settings, CancellationToken.None);

        provider.Verify(
            x => x.ExecuteQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Once, "only ValueGroundingMaxProbes probes may execute across the whole call");
    }

    [Test]
    public async Task BuildValueMatchesBlockAsync_ValueGroundingDisabled_ReturnsEmptyAndNeverTouchesTheDataSource()
    {
        var tables = new[] { new ValueGroundingTable("sales", "orders", new[] { new ValueGroundingColumn("status", "varchar", false, null, null) }) };
        var provider = new Mock<IDataSourceProvider>();
        var (service, factory) = BuildService(provider);

        var settings = NewSettings();
        settings.EnableValueGrounding = false;

        var block = await service.BuildValueMatchesBlockAsync(DataSourceId, "orders that were 'refunded'", tables, settings, CancellationToken.None);

        block.Should().Be("");
        factory.Verify(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task BuildValueMatchesBlockAsync_ApiDataSource_ReturnsEmpty()
    {
        var tables = new[] { new ValueGroundingTable("sales", "orders", new[] { new ValueGroundingColumn("status", "varchar", false, null, null) }) };
        var provider = new Mock<IDataSourceProvider>();
        var (service, _) = BuildService(provider, dataSourceType: DataSourceType.Api);

        var block = await service.BuildValueMatchesBlockAsync(DataSourceId, "orders that were 'refunded'", tables, NewSettings(), CancellationToken.None);

        block.Should().Be("");
        provider.Verify(
            x => x.ExecuteQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task BuildValueMatchesBlockAsync_NoLiteralsInQuestion_ReturnsEmptyWithoutLoadingTheDataSource()
    {
        var tables = new[] { new ValueGroundingTable("sales", "orders", new[] { new ValueGroundingColumn("status", "varchar", false, null, null) }) };
        var provider = new Mock<IDataSourceProvider>();
        var (service, factory) = BuildService(provider);

        var block = await service.BuildValueMatchesBlockAsync(DataSourceId, "how many orders exist", tables, NewSettings(), CancellationToken.None);

        block.Should().Be("");
        factory.Verify(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task BuildValueMatchesBlockAsync_ProviderThrows_FailsClosedAndReturnsEmpty()
    {
        var tables = new[] { new ValueGroundingTable("sales", "orders", new[] { new ValueGroundingColumn("status", "varchar", false, null, null) }) };
        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection lost"));
        var (service, _) = BuildService(provider);

        var act = async () => await service.BuildValueMatchesBlockAsync(
            DataSourceId, "orders that were 'refunded'", tables, NewSettings(), CancellationToken.None);

        var block = await act.Should().NotThrowAsync();
        block.Subject.Should().Be("");
    }

    [Test]
    public async Task BuildValueMatchesBlockAsync_Cancellation_RethrowsRatherThanSwallowing()
    {
        var tables = new[] { new ValueGroundingTable("sales", "orders", new[] { new ValueGroundingColumn("status", "varchar", false, null, null) }) };
        var provider = new Mock<IDataSourceProvider>();
        var (service, _) = BuildService(provider);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // The literal loop's own ThrowIfCancellationRequested() fires before any probe is attempted —
        // the outer catch must rethrow it rather than swallowing it as a probe-level failure (R11).
        var act = async () => await service.BuildValueMatchesBlockAsync(
            DataSourceId, "orders that were 'refunded'", tables, NewSettings(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task BuildValueMatchesBlockAsync_NoTables_ReturnsEmpty()
    {
        var provider = new Mock<IDataSourceProvider>();
        var (service, factory) = BuildService(provider);

        var block = await service.BuildValueMatchesBlockAsync(
            DataSourceId, "orders that were 'refunded'", [], NewSettings(), CancellationToken.None);

        block.Should().Be("");
        factory.Verify(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------- helpers ----------

    private static McpSettingsData NewSettings()
    {
        return new McpSettingsData
        {
            EnableValueGrounding = true,
            ValueGroundingMaxProbes = 12
        };
    }

    private static (ValueGroundingService Service, Mock<IDbContextFactory<BeaconContext>> Factory) BuildService(
        Mock<IDataSourceProvider> provider, DataSourceType dataSourceType = DataSourceType.Database)
    {
        var dataSource = new DataSource
        {
            Id = DataSourceId,
            Name = "ds",
            DataSourceType = dataSourceType,
            EncryptedConnectionData = "encrypted",
            DatabaseEngineType = dataSourceType == DataSourceType.Api ? null : DatabaseEngineType.PostgreSQL
        };

        var context = new ValueGroundingTestContext(BuildDbSet(new[] { dataSource }).Object);

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        var providerFactory = new Mock<IDataSourceProviderFactory>();
        providerFactory
            .Setup(x => x.GetProvider(It.IsAny<DataSourceType>()))
            .Returns(provider.Object);

        var service = new ValueGroundingService(
            factory.Object,
            providerFactory.Object,
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            NullLogger<ValueGroundingService>.Instance);

        return (service, factory);
    }

    private static Mock<DbSet<T>> BuildDbSet<T>(IEnumerable<T> data) where T : class
    {
        var queryable = data.AsQueryable();
        var set = new Mock<DbSet<T>>();
        set.As<IAsyncEnumerable<T>>()
            .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(() => new TestAsyncEnumerator<T>(data.GetEnumerator()));
        set.As<IQueryable<T>>().Setup(x => x.Provider).Returns(new TestAsyncQueryProvider<T>(queryable.Provider));
        set.As<IQueryable<T>>().Setup(x => x.Expression).Returns(queryable.Expression);
        set.As<IQueryable<T>>().Setup(x => x.ElementType).Returns(queryable.ElementType);
        set.As<IQueryable<T>>().Setup(x => x.GetEnumerator()).Returns(() => data.GetEnumerator());
        return set;
    }

    private sealed class ValueGroundingTestContext : BeaconContext
    {
        private static readonly DbContextOptions<ValueGroundingTestContext> Options =
            new DbContextOptionsBuilder<ValueGroundingTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly DbSet<DataSource> _dataSources;

        public ValueGroundingTestContext(DbSet<DataSource> dataSources) : base(Options, "beacon")
        {
            _dataSources = dataSources;
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(DataSource))
            {
                return (DbSet<TEntity>)(object)_dataSources;
            }

            return base.Set<TEntity>();
        }

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}

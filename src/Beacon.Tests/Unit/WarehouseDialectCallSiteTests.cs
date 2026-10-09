using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Ai;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.SemanticSearch;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Semantic search and alert generation validate SQL in the dialect of the data source it targets. A warehouse has no
/// engine type, so a plain SELECT only passes when the dialect comes from the data source type.
/// </summary>
[TestFixture]
public class WarehouseDialectCallSiteTests
{
    private const int WarehouseId = 5;

    [TestCase(DataSourceType.BigQuery)]
    [TestCase(DataSourceType.Databricks)]
    public async Task SemanticSearch_WarehouseSource_ValidatesInItsOwnDialect(DataSourceType type)
    {
        var result = await SemanticSearch(type, "SELECT id FROM orders").AskAsync(WarehouseId, "ids?", execute: false);

        result.Error.Should().BeNull();
        result.GeneratedSql.Should().Be("SELECT id FROM orders");
    }

    [Test]
    public async Task SemanticSearch_BackslashBeforeQuote_IsRejected()
    {
        var result = await SemanticSearch(DataSourceType.BigQuery, "SELECT 'x\\', 1 AS y").AskAsync(WarehouseId, "x?", execute: false);

        result.Error.Should().Contain("A backslash before a quote is not allowed");
    }

    [Test]
    public async Task SemanticSearch_MissingDataSource_ReportsIt()
    {
        var result = await SemanticSearch(DataSourceType.BigQuery, "SELECT id FROM orders").AskAsync(99, "ids?", execute: false);

        result.Error.Should().Be("Data source 99 not found");
    }

    [TestCase(DataSourceType.BigQuery, "SELECT id FROM orders", true)]
    [TestCase(DataSourceType.Databricks, "SELECT id FROM orders", true)]
    [TestCase(DataSourceType.BigQuery, "SELECT 'x\\', 1 AS y", false)]
    public async Task AlertSql_WarehouseSource_IsValidatedInItsOwnDialect(DataSourceType type, string sql, bool valid)
    {
        (await AlertGeneration(type).ValidateQuerySyntaxAsync(WarehouseId, sql)).Should().Be(valid);
    }

    [Test]
    public async Task AlertSql_MissingDataSource_IsInvalid()
    {
        (await AlertGeneration(DataSourceType.BigQuery).ValidateQuerySyntaxAsync(99, "SELECT id FROM orders")).Should().BeFalse();
    }

    private static SemanticSearchService SemanticSearch(DataSourceType type, string generatedSql)
    {
        var llm = new Mock<ILlmProvider>();
        llm
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = $"```sql\n{generatedSql}\n```\nExplanation: test" });

        return new SemanticSearchService(
            Factory(type).Object,
            Mock.Of<IKnowledgeGraphService>(),
            llm.Object,
            new QueryGuardrailService(),
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            Mock.Of<IDataSourceProviderFactory>(),
            NullLogger<SemanticSearchService>.Instance);
    }

    private static AiAlertGenerationService AlertGeneration(DataSourceType type)
    {
        return new AiAlertGenerationService(
            Mock.Of<ILlmProvider>(),
            Mock.Of<IDatabaseMetadataService>(),
            Factory(type).Object,
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            NullLogger<AiAlertGenerationService>.Instance);
    }

    private static Mock<IDbContextFactory<BeaconContext>> Factory(DataSourceType type)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new WarehouseContext(type));

        return factory;
    }

    /// <summary>One warehouse data source over the async-queryable doubles — no DB (§4.7).</summary>
    private sealed class WarehouseContext(DataSourceType type) : BeaconContext(Options, "beacon")
    {
        private static readonly DbContextOptions<WarehouseContext> Options =
            new DbContextOptionsBuilder<WarehouseContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) != typeof(DataSource))
            {
                return base.Set<TEntity>();
            }

            var data = new List<DataSource>
            {
                new() { Id = WarehouseId, Name = "lake", DataSourceType = type, EncryptedConnectionData = "encrypted" }
            }.AsQueryable();
            var set = new Mock<DbSet<DataSource>>();
            set.As<IAsyncEnumerable<DataSource>>()
                .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                .Returns(() => new TestAsyncEnumerator<DataSource>(data.GetEnumerator()));
            set.As<IQueryable<DataSource>>()
                .Setup(x => x.Provider)
                .Returns(new TestAsyncQueryProvider<DataSource>(data.Provider));
            set.As<IQueryable<DataSource>>().Setup(x => x.Expression).Returns(data.Expression);
            set.As<IQueryable<DataSource>>().Setup(x => x.ElementType).Returns(data.ElementType);
            set.As<IQueryable<DataSource>>().Setup(x => x.GetEnumerator()).Returns(() => data.GetEnumerator());

            return (DbSet<TEntity>)(object)set.Object;
        }
    }
}

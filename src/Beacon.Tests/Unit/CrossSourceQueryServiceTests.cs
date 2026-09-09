using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Ai;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.MCP.Services;
using Beacon.Tests.Common;
using ProviderValidationResult = Beacon.Core.Models.Providers.QueryValidationResult;

namespace Beacon.Tests.Unit;

/// <summary>
/// The cross-source <c>ask</c> path's gate wiring (spec <c>sql-execution-gate</c>, caller-mapping rows for
/// <c>CrossSourceQueryService</c>): loop 1 acts on the schema verdict only; loop 2 runs the read-only gate
/// BEFORE the provider dry-run (a write must never reach EXPLAIN), adopts a dry-run repair only when it clears
/// the gate, and executes the gate's <c>FinalSql</c> capped by <c>settings.MaxRowLimit</c>; the LLM join SQL
/// is gated too. Real gate over the real validators and guardrail; provider, knowledge graph, SQL generation
/// and LLM are mocked; the in-memory SQLite join store is real (§4.7 — no Beacon DB).
/// </summary>
[TestFixture]
public class CrossSourceQueryServiceTests
{
    private const int ProjectId = 42;
    private const int DataSourceId = 7;
    private const int MaxRowLimit = 250;

    private Mock<IDataSourceProvider> _provider = null!;
    private Mock<ISqlGenerationService> _sqlGen = null!;
    private Mock<ILlmProvider> _llm = null!;
    private McpSignalBuilder _signal = null!;

    [SetUp]
    public void SetUp()
    {
        _provider = new Mock<IDataSourceProvider>();
        _provider
            .Setup(x => x.ValidateQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderValidationResult { IsValid = true });
        _provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult
            {
                Success = true,
                Rows = [new Dictionary<string, object?> { ["id"] = 1 }]
            });

        _sqlGen = new Mock<ISqlGenerationService>();
        _llm = new Mock<ILlmProvider>();
        _llm
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = "SELECT * FROM result1" });

        _signal = new McpSignalBuilder().SetTool("ask").SetQuestion("q");
    }

    [Test]
    public async Task WriteStatement_IsBlockedBeforeProviderDryRun_AndNeverExecuted()
    {
        Generates("DELETE FROM orders");

        var (text, succeeded) = await CreateService().ExecuteAsync(
            _llm.Object, Sources(), ProjectId, "q", Settings(), execute: true, _signal, CancellationToken.None);

        succeeded.Should().BeFalse();
        text.Should().Contain("Validation Error for warehouse");
        _provider.Verify(
            x => x.ValidateQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a write statement must never reach the provider dry-run (EXPLAIN)");
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task ExecutedSql_IsCappedBySettingsMaxRowLimit_NotAConstant()
    {
        Generates("SELECT id FROM orders");

        var (text, succeeded) = await CreateService().ExecuteAsync(
            _llm.Object, Sources(), ProjectId, "q", Settings(), execute: true, _signal, CancellationToken.None);

        succeeded.Should().BeTrue(text);
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(
                It.IsAny<DataSource>(),
                $"SELECT id FROM orders LIMIT {MaxRowLimit}",
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the per-source cap comes from the operator's MaxRowLimit, applied to the SQL that actually runs");
        text.Should().Contain("### Final Results");
    }

    [Test]
    public async Task SchemaFailure_RetryThatClearsTheGate_IsAdoptedAndExecuted()
    {
        Generates("SELECT bogus FROM orders");
        _sqlGen
            .Setup(x => x.RetryWithErrorAsync(
                It.IsAny<ILlmProvider>(), It.IsAny<string>(), "SELECT bogus FROM orders", It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("SELECT id FROM orders");

        var (text, succeeded) = await CreateService().ExecuteAsync(
            _llm.Object, Sources(), ProjectId, "q", Settings(), execute: true, _signal, CancellationToken.None);

        succeeded.Should().BeTrue(text);
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(
                It.IsAny<DataSource>(),
                It.Is<string>(s => s.StartsWith("SELECT id FROM orders")),
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _signal.Build().SchemaValidationFailed.Should().BeTrue();
        _signal.Build().RetrySucceeded.Should().BeTrue();
    }

    [Test]
    public async Task SchemaFailure_RetryThatStillFails_ExcludesTheSource()
    {
        Generates("SELECT bogus FROM orders");
        _sqlGen
            .Setup(x => x.RetryWithErrorAsync(
                It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("SELECT still_bogus FROM orders");

        var (text, succeeded) = await CreateService().ExecuteAsync(
            _llm.Object, Sources(), ProjectId, "q", Settings(), execute: true, _signal, CancellationToken.None);

        succeeded.Should().BeFalse();
        text.Should().Contain("Schema Validation Error");
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task DryRunRepair_RetryThatFailsReadOnly_IsNotAdopted()
    {
        Generates("SELECT id FROM orders");
        _provider
            .SetupSequence(x => x.ValidateQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderValidationResult { IsValid = false, Errors = ["relation missing"] })
            .ReturnsAsync(new ProviderValidationResult { IsValid = true });
        _sqlGen
            .Setup(x => x.RetryWithErrorAsync(
                It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("DELETE FROM orders");

        var (text, succeeded) = await CreateService().ExecuteAsync(
            _llm.Object, Sources(), ProjectId, "q", Settings(), execute: true, _signal, CancellationToken.None);

        succeeded.Should().BeTrue(text);
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(
                It.IsAny<DataSource>(),
                It.Is<string>(s => s.StartsWith("SELECT id FROM orders")),
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "a repair that fails the read-only gate is dropped and the original SQL runs");
    }

    [Test]
    public async Task JoinSql_ThatFailsReadOnly_IsRefused()
    {
        Generates("SELECT id FROM orders");
        _llm
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = "DELETE FROM result1" });

        var (text, succeeded) = await CreateService().ExecuteAsync(
            _llm.Object, Sources(), ProjectId, "q", Settings(), execute: true, _signal, CancellationToken.None);

        succeeded.Should().BeFalse();
        text.Should().Contain("Validation Error for join query");
    }

    [Test]
    public async Task PreviewMode_RendersSqlWithoutExecuting()
    {
        Generates("SELECT id FROM orders");

        var (text, succeeded) = await CreateService().ExecuteAsync(
            _llm.Object, Sources(), ProjectId, "q", Settings(), execute: false, _signal, CancellationToken.None);

        succeeded.Should().BeTrue();
        text.Should().Contain("Execution skipped");
        _provider.VerifyNoOtherCalls();
    }

    private void Generates(string sql)
    {
        _sqlGen
            .Setup(x => x.GenerateAsync(
                It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(sql, ["orders"]));
    }

    private static List<RoutedSource> Sources() =>
        [new RoutedSource { DataSourceId = DataSourceId, DataSourceName = "warehouse", Reason = "test" }];

    private static McpSettingsData Settings() => new() { MaxRowLimit = MaxRowLimit, EnableSemanticLint = false };

    private static Dictionary<string, HashSet<string>> Catalog() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["orders"] = new(StringComparer.OrdinalIgnoreCase) { "id", "customer_id", "total" }
    };

    private CrossSourceQueryService CreateService()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new CrossSourceTestContext());

        var providerFactory = new Mock<IDataSourceProviderFactory>();
        providerFactory
            .Setup(x => x.GetProvider(It.IsAny<DataSourceType>()))
            .Returns(_provider.Object);

        var knowledgeGraph = new Mock<IKnowledgeGraphService>();
        knowledgeGraph
            .Setup(x => x.GetSmartContextForAskAsync(DataSourceId, ProjectId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SmartSchemaContext
            {
                FullContext = "schema context",
                DatabaseDialect = "PostgreSQL",
                SchemaCatalog = Catalog()
            });

        var guardrail = new QueryGuardrailService();

        return new CrossSourceQueryService(
            factory.Object,
            providerFactory.Object,
            guardrail,
            TestSqlGate.Create(guardrail),
            knowledgeGraph.Object,
            _sqlGen.Object,
            NullLoggerFactory.Instance,
            NullLogger<CrossSourceQueryService>.Instance);
    }

    /// <summary>Serves the data-source lookup over an async-queryable double — no DB (§4.7).</summary>
    private sealed class CrossSourceTestContext : BeaconContext
    {
        private static readonly DbContextOptions<CrossSourceTestContext> Options =
            new DbContextOptionsBuilder<CrossSourceTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        public CrossSourceTestContext() : base(Options, "beacon")
        {
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(DataSource))
            {
                return (DbSet<TEntity>)(object)BuildSet(new List<DataSource>
                {
                    new()
                    {
                        Id = DataSourceId,
                        Name = "warehouse",
                        DataSourceType = DataSourceType.Database,
                        EncryptedConnectionData = "encrypted",
                        DatabaseEngineType = DatabaseEngineType.PostgreSQL
                    }
                });
            }

            return base.Set<TEntity>();
        }

        private static DbSet<T> BuildSet<T>(List<T> data) where T : class
        {
            var queryable = data.AsQueryable();
            var set = new Mock<DbSet<T>>();
            set.As<IAsyncEnumerable<T>>()
                .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                .Returns(() => new TestAsyncEnumerator<T>(queryable.GetEnumerator()));
            set.As<IQueryable<T>>()
                .Setup(x => x.Provider)
                .Returns(new TestAsyncQueryProvider<T>(queryable.Provider));
            set.As<IQueryable<T>>().Setup(x => x.Expression).Returns(queryable.Expression);
            set.As<IQueryable<T>>().Setup(x => x.ElementType).Returns(queryable.ElementType);
            set.As<IQueryable<T>>().Setup(x => x.GetEnumerator()).Returns(() => queryable.GetEnumerator());
            return set.Object;
        }
    }
}

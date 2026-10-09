using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC4 of spec <c>sql-execution-gate</c>: the <c>query</c> tool now runs the schema-catalog gate and it is
/// BLOCKING — a hallucinated column is refused before anything reaches the provider. With no catalog
/// the gate reports Skipped and the query executes as before. Real gate over the real validators
/// (guardrail included); data-source resolution over async-queryable doubles (§4.7).
/// </summary>
[TestFixture]
public class ProjectQueryToolSchemaGateTests
{
    private const int ProjectId = 42;
    private const int DataSourceId = 7;

    private Mock<IDataSourceProvider> _provider = null!;
    private Mock<IKnowledgeGraphService> _knowledgeGraph = null!;
    private List<McpQuerySignal> _signals = null!;
    private RecordingSqlGate _gate = null!;
    private DataSourceType _warehouseType;
    private DatabaseEngineType? _warehouseEngine;

    [SetUp]
    public void SetUp()
    {
        _provider = new Mock<IDataSourceProvider>();
        _provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(
                It.IsAny<DataSource>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult
            {
                Success = true,
                Rows = [new Dictionary<string, object?> { ["id"] = 1 }]
            });

        _knowledgeGraph = new Mock<IKnowledgeGraphService>();
        _signals = [];
        _warehouseType = DataSourceType.Database;
        _warehouseEngine = DatabaseEngineType.PostgreSQL;
    }

    [TestCase(DataSourceType.BigQuery, "bigquery")]
    [TestCase(DataSourceType.Databricks, "databricks")]
    public async Task WarehouseWithoutEngineType_IsGatedInItsOwnDialect(DataSourceType type, string dialect)
    {
        _warehouseType = type;
        _warehouseEngine = null;
        WithCatalog(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase));

        var result = await CreateTool().ExecuteAsync(
            datasource_id: DataSourceId, sql: "SELECT id FROM orders", max_rows: 25, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        _gate.Dialects.Should().Equal(dialect);
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), "SELECT id FROM orders LIMIT 25", It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task UnknownColumn_WithCatalog_IsRefused_AndProviderNeverCalled()
    {
        WithCatalog(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["orders"] = ["id", "customer_id", "total"]
        });

        var result = await CreateTool().ExecuteAsync(
            datasource_id: DataSourceId, sql: "SELECT bogus FROM orders", cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeTrue();
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        text.Should().StartWith("Query validation failed: ");
        text.Should().Contain("Column 'bogus' does not exist");

        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a statement refused by the schema gate must never reach the connector");

        // §9.5 — the refusal is still a recorded signal, with the AST-resolved tables.
        _signals.Should().ContainSingle();
        _signals[0].IsSuccessful.Should().BeFalse();
        _signals[0].ExecutionError.Should().Contain("bogus");
        _signals[0].TablesUsed.Should().Contain("orders");
    }

    [Test]
    public async Task EmptyCatalog_SchemaSkipped_QueryExecutesWithRowLimit()
    {
        WithCatalog(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase));

        var result = await CreateTool().ExecuteAsync(
            datasource_id: DataSourceId, sql: "SELECT bogus FROM orders", max_rows: 25, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(
                It.IsAny<DataSource>(),
                "SELECT bogus FROM orders LIMIT 25",
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "without a catalog the schema gate is Skipped, and the row cap comes from the gate's FinalSql");

        _signals.Should().ContainSingle();
        _signals[0].IsSuccessful.Should().BeTrue();
        _signals[0].GeneratedSql.Should().Be("SELECT bogus FROM orders");
    }

    [Test]
    public async Task KnownColumns_WithCatalog_Execute_AndTablesResolvedFromAst()
    {
        WithCatalog(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["orders"] = ["id", "customer_id", "total"],
            ["customers"] = ["id", "name"]
        });

        var result = await CreateTool().ExecuteAsync(
            datasource_id: DataSourceId,
            sql: "WITH recent AS (SELECT * FROM orders o) SELECT c.name FROM recent JOIN customers c ON c.id = recent.customer_id",
            cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        _signals.Should().ContainSingle();
        _signals[0].TablesUsed.Should().Contain("orders").And.Contain("customers");
        _signals[0].TablesUsed.Should().NotContain("recent", "a CTE name is not a table");
    }

    [Test]
    public async Task ProjectMaxRowLimitOverride_CapsTheQuery_BelowTheRequestedAndGlobalLimits()
    {
        // T-F002 / SF-F001: the tool resolves settings for the AUTHORIZED project, not the global row. Global
        // allows 1000 rows, project 42 is capped at 10, the caller asks for 25 — the project cap wins.
        WithCatalog(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase));
        var settingsProvider = SettingsProviderMock.Create(
            new McpSettingsData { MaxRowLimit = 1000 },
            projectSettings: new Dictionary<int, McpSettingsData> { [ProjectId] = new McpSettingsData { MaxRowLimit = 10 } });

        var result = await CreateTool(settingsProvider).ExecuteAsync(
            datasource_id: DataSourceId, sql: "SELECT id FROM orders", max_rows: 25, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        _provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(
                It.IsAny<DataSource>(),
                "SELECT id FROM orders LIMIT 10",
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the project's MaxRowLimit override caps the query below both the requested 25 and the global 1000");
        settingsProvider.Verify(x => x.GetEffectiveSettingsAsync(ProjectId, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        settingsProvider.Verify(x => x.GetEffectiveSettingsAsync(It.Is<int>(id => id != ProjectId), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestCase("SELECT * FROM orders", "email")]
    [TestCase("SELECT * FROM orders", "customer_email")]
    [TestCase("SELECT * FROM orders", "email_address")]
    public async Task PiiResultColumns_AreMasked_EvenWhenTheSqlTextDoesNotNameThem(string sql, string resultColumn)
    {
        WithCatalog(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase));
        _provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult
            {
                Success = true,
                Rows = [new Dictionary<string, object?> { [resultColumn] = "ada@example.com", ["id"] = 1 }]
            });

        var result = await CreateTool().ExecuteAsync(datasource_id: DataSourceId, sql: sql, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(x => x.Text));
        text.Should().NotContain("ada@example.com");
        text.Should().Contain("a***m");
    }

    // SC7: the result key "contact" is not PII by name and the SQL-text match is "email" — only the executed SQL's
    // alias resolution masks it.
    [TestCase("SELECT email AS contact, id FROM orders")]
    [TestCase("SELECT upper(o.email) AS contact, o.id FROM orders o")]
    public async Task AnAliasedPiiColumn_IsMasked(string sql)
    {
        WithCatalog(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase));
        _provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult
            {
                Success = true,
                Rows = [new Dictionary<string, object?> { ["contact"] = "ada@example.com", ["id"] = 1 }]
            });

        var result = await CreateTool().ExecuteAsync(datasource_id: DataSourceId, sql: sql, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(x => x.Text));
        text.Should().NotContain("ada@example.com");
        text.Should().Contain("a***m");
    }

    private void WithCatalog(Dictionary<string, HashSet<string>> catalog)
    {
        _knowledgeGraph
            .Setup(x => x.GetSchemaCatalogAsync(DataSourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(catalog);
    }

    private ProjectQueryTool CreateTool(Mock<IMcpSettingsProvider>? settingsProvider = null)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SchemaGateTestContext(_signals, _warehouseType, _warehouseEngine));

        var providerFactory = new Mock<IDataSourceProviderFactory>();
        providerFactory
            .Setup(x => x.GetProvider(It.IsAny<DataSourceType>()))
            .Returns(_provider.Object);

        settingsProvider ??= SettingsProviderMock.Create();

        var projectContext = new McpProjectContext { UserId = 1, AllowedProjectIds = [ProjectId] };
        var guardrail = new QueryGuardrailService();
        _gate = new RecordingSqlGate(TestSqlGate.Create(guardrail));

        // Audit rows are asserted elsewhere (DryRunToolTests); a bare factory mock keeps §1.7 paths runnable.
        var auditService = new McpAuditService(new Mock<IDbContextFactory<BeaconContext>>().Object, SettingsProviderMock.Create().Object, new HttpContextAccessor(), Options.Create(new McpDeploymentOptions()), NullLogger<McpAuditService>.Instance, new McpAuditOutcome(), Options.Create(new BeaconTelemetryOptions()), NullLoggerFactory.Instance);
        var signalService = new McpSignalService(factory.Object, settingsProvider.Object, NullLogger<McpSignalService>.Instance);

        return new ProjectQueryTool(
            factory.Object,
            providerFactory.Object,
            guardrail,
            _gate,
            _knowledgeGraph.Object,
            settingsProvider.Object,
            projectContext,
            auditService,
            signalService,
            NullLogger<ProjectQueryTool>.Instance);
    }

    /// <summary>Data-source resolution over async-queryable doubles plus signal capture — no DB (§4.7).</summary>
    private sealed class SchemaGateTestContext : BeaconContext
    {
        private static readonly DbContextOptions<SchemaGateTestContext> Options =
            new DbContextOptionsBuilder<SchemaGateTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly Mock<DbSet<McpQuerySignal>> _signalSet = new();
        private readonly DataSourceType _warehouseType;
        private readonly DatabaseEngineType? _warehouseEngine;

        public SchemaGateTestContext(List<McpQuerySignal> signals, DataSourceType warehouseType, DatabaseEngineType? warehouseEngine)
            : base(Options, "beacon")
        {
            _warehouseType = warehouseType;
            _warehouseEngine = warehouseEngine;
            _signalSet.Setup(x => x.Add(It.IsAny<McpQuerySignal>()))
                .Callback<McpQuerySignal>(signals.Add);
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpQuerySignal))
            {
                return (DbSet<TEntity>)(object)_signalSet.Object;
            }

            if (typeof(TEntity) == typeof(DataSource))
            {
                return (DbSet<TEntity>)(object)BuildSet(new List<DataSource>
                {
                    new()
                    {
                        Id = DataSourceId,
                        Name = "warehouse",
                        DataSourceType = _warehouseType,
                        EncryptedConnectionData = "encrypted",
                        DatabaseEngineType = _warehouseEngine
                    }
                });
            }

            if (typeof(TEntity) == typeof(ProjectDataSource))
            {
                return (DbSet<TEntity>)(object)BuildSet(new List<ProjectDataSource>
                {
                    new() { ProjectId = ProjectId, DataSourceId = DataSourceId }
                });
            }

            return base.Set<TEntity>();
        }

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

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

using System.Collections.Concurrent;
using System.Data.Common;
using System.Reflection;
using Beacon.AI.Services.Embeddings;
using Beacon.AI.Services.Eval;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Connector.BigQuery;
using Beacon.Connector.Databricks;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Exceptions;
using Beacon.Core.Helpers;
using Beacon.Core.HostData;
using Beacon.Core.Models;
using Beacon.Core.Models.Ai;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Models.Providers;
using Beacon.Core.Models.Queries;
using Beacon.Core.SavedQueries;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.SavedQueryTools;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using ProviderValidationResult = Beacon.Core.Models.Providers.QueryValidationResult;
using Beacon.Core.Mcp;

namespace Beacon.Tests.Unit;

/// <summary>
/// Every path that runs or checks SQL against a data source hands the gate the database the source's connection is
/// configured for: a name qualified with that database reaches execution (the provider, the connection, the executor),
/// a name qualified with another one is refused before it. The paths: the paged step preview, the provider dry-runs of
/// databases, Databricks and BigQuery, dry_run, saved-query tools, eval runs, the ask pipeline and the
/// cross-source ask (its schema, read-only and repair gates), and the grounding context that carries the configured
/// database to the ask paths. Nothing reaches a database: providers are mocked and a connection stops at its factory.
/// </summary>
[TestFixture]
[NonParallelizable] // DbConnectionFactory is a process-wide registry.
public class CrossDatabaseCallSiteTests
{
    private const int ProjectId = 1;
    private const int DataSourceId = 5;
    private const string Configured = "sales";
    private const string Matching = "SELECT id FROM sales.public.orders";
    private const string Foreign = "SELECT id FROM hr.public.orders";

    private Func<string, DbConnection>? _previousPostgreSqlFactory;
    private int _connectionsOpened;

    [SetUp]
    public void SetUp()
    {
        Registry().TryGetValue(DatabaseEngineType.PostgreSQL, out _previousPostgreSqlFactory);
        _connectionsOpened = 0;
        Registry()[DatabaseEngineType.PostgreSQL] = _ =>
        {
            _connectionsOpened++;
            throw new ConnectionReachedException();
        };
    }

    [TearDown]
    public void TearDown()
    {
        if (_previousPostgreSqlFactory == null)
        {
            Registry().TryRemove(DatabaseEngineType.PostgreSQL, out _);
            return;
        }

        Registry()[DatabaseEngineType.PostgreSQL] = _previousPostgreSqlFactory;
    }

    [Test]
    public async Task PagedStepPreview_RefusesAnotherDatabase_BeforeConnecting()
    {
        var service = QueryService();

        var matching = () => service.PreviewQueryStepPaged(1, 1, null, Draft(Matching), FirstPage, CancellationToken.None);
        var foreign = () => service.PreviewQueryStepPaged(1, 1, null, Draft(Foreign), FirstPage, CancellationToken.None);

        await foreign.Should().ThrowAsync<InvalidOperationException>().WithMessage("*another database*");
        _connectionsOpened.Should().Be(0);
        // A failure on the data source ends the preview as a failed page, carrying the provider's message — redacted, as
        // PII detection is on — and the connection was reached.
        var page = await matching();
        page.Success.Should().BeFalse();
        page.ErrorMessage.Should().StartWith("Database error");
        _connectionsOpened.Should().Be(1);
    }

    [Test]
    public async Task DatabaseProviderDryRun_RefusesAnotherDatabase_BeforeConnecting()
    {
        var provider = new DatabaseProvider(
            Connections(),
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            AllowingHostGuard(),
            new ConfiguredDatabaseResolver(Connections(), NullLogger<ConfiguredDatabaseResolver>.Instance),
            NullLogger<DatabaseProvider>.Instance,
            TestQueryServices.QueryOptions);

        var foreign = await provider.ValidateQueryAsync(Source(), Foreign);
        _connectionsOpened.Should().Be(0);
        var matching = await provider.ValidateQueryAsync(Source(), Matching);

        foreign.IsValid.Should().BeFalse();
        foreign.Errors.Should().ContainSingle().Which.Should().Contain("another database");
        _connectionsOpened.Should().Be(1, "the configured database's name passes the check and the dry-run opens the connection");
        matching.Errors.Should().NotContain(x => x.Contains("another database"));
    }

    [Test]
    public async Task DatabricksDryRun_AcceptsTheConfiguredCatalog_AndRefusesAnother()
    {
        var encryption = new Mock<IEncryptionService>();
        encryption
            .Setup(x => x.Decrypt(It.IsAny<string>()))
            .Returns("""{"Host":"h","HttpPath":"/sql/1.0/warehouses/w","Token":"t","Catalog":"main"}""");
        var provider = new DatabricksProvider(
            Mock.Of<IHttpClientFactory>(MockBehavior.Strict),
            TestTransport.Resolver(encryption.Object),
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            NullLogger<DatabricksProvider>.Instance);
        var dataSource = new DataSource { Id = DataSourceId, Name = "lakehouse", DataSourceType = DataSourceType.Databricks, EncryptedConnectionData = "enc" };

        var matching = await provider.ValidateQueryAsync(dataSource, "SELECT id FROM main.sales.orders");
        var foreign = await provider.ValidateQueryAsync(dataSource, "SELECT id FROM hive_metastore.sales.orders");

        matching.IsValid.Should().BeTrue(string.Join("; ", matching.Errors));
        foreign.IsValid.Should().BeFalse();
        foreign.Errors.Should().ContainSingle().Which.Should().Contain("another catalog");
    }

    [Test]
    public async Task BigQueryDryRun_AcceptsTheConfiguredProject_AndRefusesAnother()
    {
        var encryption = new Mock<IEncryptionService>();
        encryption
            .Setup(x => x.Decrypt(It.IsAny<string>()))
            .Returns("""{"ProjectId":"acme-prod","DatasetId":"sales"}""");
        var provider = new BigQueryProvider(
            TestTransport.Resolver(encryption.Object),
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            NullLogger<BigQueryProvider>.Instance);
        var dataSource = new DataSource { Id = DataSourceId, Name = "warehouse", DataSourceType = DataSourceType.BigQuery, EncryptedConnectionData = "enc" };

        var matching = await provider.ValidateQueryAsync(dataSource, "SELECT id FROM `acme-prod.sales.orders`");
        var foreign = await provider.ValidateQueryAsync(dataSource, "SELECT id FROM `acme-dev.sales.orders`");

        matching.IsValid.Should().BeTrue(string.Join("; ", matching.Errors));
        foreign.IsValid.Should().BeFalse();
        foreign.Errors.Should().ContainSingle().Which.Should().Contain("another project");
    }

    [Test]
    public async Task DryRun_RunsTheProviderDryRunForTheConfiguredDatabase_AndRefusesAnother()
    {
        var queryExecution = new Mock<IQueryExecutionService>();
        queryExecution
            .Setup(x => x.ValidateAsync(DataSourceId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderDryRunOutcome.Valid());
        var knowledge = new Mock<IKnowledgeGraphService>();
        knowledge
            .Setup(x => x.GetSchemaCatalogAsync(DataSourceId, ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase));
        var store = Store();
        var settings = SettingsProviderMock.Create();
        var tool = new DryRunTool(
            store.Factory(),
            TestSqlGate.Create(),
            TestConfiguredDatabases.Of(Configured),
            TestRowSecurity.Resolver,
            knowledge.Object,
            queryExecution.Object,
            SettingsProviderMock.Unshared(settings.Object),
            new McpProjectContext { UserId = 1, AllowedProjectIds = [ProjectId], QueryProjectIds = [ProjectId] },
            new McpAuditService(store.Factory(), settings.Object, new HttpContextAccessor(), Options.Create(new McpDeploymentOptions()), NullLogger<McpAuditService>.Instance, new McpAuditOutcome(), Options.Create(new BeaconTelemetryOptions()), NullLoggerFactory.Instance, TestMcpAudit.ContentHasher, new McpStatementLog()),
            new McpSignalService(store.Factory(), settings.Object, NullLogger<McpSignalService>.Instance),
            new McpQueryConcurrencyLimiter(),
            NullLogger<DryRunTool>.Instance);

        var foreign = await tool.ExecuteAsync(datasource_id: DataSourceId, sql: Foreign, cancellationToken: CancellationToken.None);
        queryExecution.Verify(x => x.ValidateAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var matching = await tool.ExecuteAsync(datasource_id: DataSourceId, sql: Matching, cancellationToken: CancellationToken.None);

        foreign.StructuredContent!.Value.GetProperty("valid").GetBoolean().Should().BeFalse();
        foreign.StructuredContent!.Value.GetProperty("issues").ToString().Should().Contain("another database");
        matching.StructuredContent!.Value.GetProperty("issues").EnumerateArray()
            .Select(x => x.GetProperty("code").GetString())
            .Should().Equal([SqlGateCodes.EmptyCatalog], "only the advisory for the missing metadata remains");
        queryExecution.Verify(x => x.ValidateAsync(DataSourceId, Matching, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task SavedQueryTool_RunsAStepInTheConfiguredDatabase_AndRefusesAnother()
    {
        var data = new SavedQueryTestData();
        data.Project(ProjectId, "A", data.DataSource(DataSourceId, "warehouse"));
        var provider = ExecutingProvider();
        var executor = new SavedQueryToolExecutor(
            data.Store.Factory().Object,
            Providers(provider.Object),
            TestSqlGate.Create(),
            TestConfiguredDatabases.Of(Configured),
            new QueryGuardrailService(),
            SettingsProviderMock.Unshared(SettingsProviderMock.Create().Object),
            TestProjectAccess.Evaluator(TestProjectAccess.ReadableIn(ProjectId)),
            TestRowSecurity.Resolver,
            TestQueryCostLimit.Passing,
            NullLoggerFactory.Instance);

        var foreign = await executor.ExecuteAsync(SavedQueryTestData.Tool("orders", [SavedQueryTestData.Step(1, DataSourceId, Foreign)], [ProjectId]), ProjectId, new Dictionary<string, object?>(), CancellationToken.None);
        ProviderRan(provider).Should().BeFalse();
        var matching = await executor.ExecuteAsync(SavedQueryTestData.Tool("orders", [SavedQueryTestData.Step(1, DataSourceId, Matching)], [ProjectId]), ProjectId, new Dictionary<string, object?>(), CancellationToken.None);

        foreign.Success.Should().BeFalse();
        foreign.Error.Should().Contain("another database");
        matching.Success.Should().BeTrue(matching.Error);
        ProviderRan(provider).Should().BeTrue();
    }

    [Test]
    public async Task EvalRun_ExecutesTheConfiguredDatabase_AndRefusesAnother()
    {
        var provider = ExecutingProvider();
        var executor = new EvalReadOnlySqlExecutor(
            Store().Factory(),
            Providers(provider.Object),
            TestSqlGate.Create(),
            TestConfiguredDatabases.Of(Configured),
            TestRowSecurity.Resolver,
            new McpSettingsData());
        executor.UseDataSource(Source());

        var foreign = await executor.ExecuteReadOnlyAsync(DataSourceId, Foreign, CancellationToken.None);
        ProviderRan(provider).Should().BeFalse();
        var matching = await executor.ExecuteReadOnlyAsync(DataSourceId, Matching, CancellationToken.None);

        foreign.Success.Should().BeFalse();
        foreign.ErrorMessage.Should().Contain("another database");
        matching.Success.Should().BeTrue();
        ProviderRan(provider).Should().BeTrue();
    }

    // The ask paths gate on the configured database the grounding context carries: on the small-schema path (every
    // table) and on the retrieval path (more than 40 tables).
    [TestCase(1, false)]
    [TestCase(41, true)]
    public async Task GroundingContext_CarriesTheConfiguredDatabase_OnBothPaths(int tableCount, bool smartPath)
    {
        var tables = Enumerable.Range(0, tableCount)
            .Select(x => Table(x == 0 ? "orders" : $"table_{x}"))
            .ToList();
        var service = KnowledgeGraph(tables);

        var context = await service.GetSmartContextForAskAsync(DataSourceId, ProjectId, "orders by status", liveValueProbes: false, CancellationToken.None);

        context.UsedSmartRetrieval.Should().Be(smartPath);
        context.ConfiguredDatabase.Should().Be(Configured);
    }

    [Test]
    public async Task AskPipeline_ExecutesTheConfiguredDatabase_AndRefusesAnother()
    {
        var knowledge = new Mock<IKnowledgeGraphService>();
        knowledge
            .Setup(x => x.GetSmartContextForAskAsync(DataSourceId, ProjectId, It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SmartSchemaContext { FullContext = "schema", DatabaseDialect = "PostgreSQL", TotalTableCount = 1, ConfiguredDatabase = Configured });
        var generation = new Mock<ISqlGenerationService>();
        var executor = new Mock<IAskSqlExecutor>();
        executor
            .Setup(x => x.ExecuteAsync(DataSourceId, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (1 row)\n", null, 1, true, [new Dictionary<string, object?> { ["id"] = 1 }]));
        var pipeline = new AskSqlPipeline(knowledge.Object, generation.Object, TestSqlGate.Create(), TestRowSecurity.Resolver, TestRowDataEgress.Discarding(), Options.Create(new McpDeploymentOptions()), new McpStatementLog(), NullLogger<AskSqlPipeline>.Instance);

        Generates(generation, Foreign);
        var foreign = await pipeline.RunAsync(Mock.Of<ILlmProvider>(), DataSourceId, ProjectId, "orders?", new McpSettingsData(), executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);
        executor.Verify(x => x.ExecuteAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        Generates(generation, Matching);
        var matching = await pipeline.RunAsync(Mock.Of<ILlmProvider>(), DataSourceId, ProjectId, "orders?", new McpSettingsData(), executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        foreign.Succeeded.Should().BeFalse();
        foreign.Text.Should().Contain("another database");
        matching.Succeeded.Should().BeTrue();
        executor.Verify(x => x.ExecuteAsync(DataSourceId, It.Is<string>(y => y.Contains("sales.public.orders")), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // The schema gate takes the grounding context's configured database, the read-only and final gates the resolver's.
    [Test]
    public async Task CrossSourceAsk_ExecutesTheConfiguredDatabase_AndRefusesAnother()
    {
        var provider = ExecutingProvider();
        var generation = new Mock<ISqlGenerationService>();
        var service = CrossSource(provider, generation);

        Generates(generation, Foreign);
        var (foreignText, foreignRan) = await service.ExecuteAsync(JoiningLlm(), [Routed()], ProjectId, "orders?", new McpSettingsData(), true, new McpSignalBuilder(), CancellationToken.None);
        ProviderRan(provider).Should().BeFalse();
        Generates(generation, Matching);
        await service.ExecuteAsync(JoiningLlm(), [Routed()], ProjectId, "orders?", new McpSettingsData(), true, new McpSignalBuilder(), CancellationToken.None);

        foreignRan.Should().BeFalse();
        foreignText.Should().Contain("another database");
        provider.Verify(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.Is<string>(y => y.Contains("sales.public.orders")), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // A dry-run repair is adopted only when it clears the gate with the source's configured database.
    [TestCase("SELECT id, status FROM sales.public.orders", true)]
    [TestCase("SELECT id, status FROM hr.public.orders", false)]
    public async Task CrossSourceAsk_AdoptsADryRunRepair_OnlyInTheConfiguredDatabase(string repaired, bool adopted)
    {
        var provider = ExecutingProvider();
        provider
            .Setup(x => x.ValidateQueryAsync(It.IsAny<DataSource>(), Matching, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderValidationResult { IsValid = false, Errors = ["column status is missing"] });
        var generation = new Mock<ISqlGenerationService>();
        generation
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), Matching, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(repaired);
        Generates(generation, Matching);

        await CrossSource(provider, generation).ExecuteAsync(JoiningLlm(), [Routed()], ProjectId, "orders?", new McpSettingsData(), true, new McpSignalBuilder(), CancellationToken.None);

        var executed = adopted ? "id, status FROM sales.public.orders" : "SELECT id FROM sales.public.orders";
        provider.Verify(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.Is<string>(y => y.Contains(executed)), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()), Times.Once);
        provider.Verify(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.Is<string>(y => y.Contains("hr.")), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static readonly ListRequest FirstPage = new Paging { Page = 0, PageSize = 20 };

    private static DataSource Source()
    {
        return new DataSource
        {
            Id = DataSourceId,
            Name = "warehouse",
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = DatabaseEngineType.PostgreSQL,
            EncryptedConnectionData = "enc"
        };
    }

    private static IDataSourceConnectionResolver Connections()
    {
        return Mock.Of<IDataSourceConnectionResolver>(x => x.GetConnectionString(It.IsAny<DataSource>()) == "Host=db;Database=sales;Username=reader");
    }

    private static IHostDataSourceGuard AllowingHostGuard()
    {
        var allowed = new HostPolicyResult(true, null, new List<string>());

        return Mock.Of<IHostDataSourceGuard>(x => x.Check(It.IsAny<DataSource>(), It.IsAny<string>()) == allowed);
    }

    private static QueryDraft Draft(string sql)
    {
        return new QueryDraft
        {
            Steps = [new QueryStepData { StepOrder = 1, Name = "step", SqlValue = sql, DataSourceId = DataSourceId }]
        };
    }

    private static Mock<IDataSourceProvider> ExecutingProvider()
    {
        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult { Success = true, Rows = [], TotalRows = 0 });
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult { Success = true, Rows = [], TotalRows = 0 });
        provider
            .Setup(x => x.ValidateQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderValidationResult { IsValid = true });

        return provider;
    }

    private static bool ProviderRan(Mock<IDataSourceProvider> provider)
    {
        return provider.Invocations.Any(x => x.Method.Name == nameof(IDataSourceProvider.ExecuteReadOnlyQueryAsync));
    }

    private static IDataSourceProviderFactory Providers(IDataSourceProvider provider)
    {
        return Mock.Of<IDataSourceProviderFactory>(x => x.GetProvider(It.IsAny<DataSourceType>()) == provider);
    }

    private static void Generates(Mock<ISqlGenerationService> generation, string sql)
    {
        generation
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(sql, ["orders"]));
    }

    // Writes the join over the loaded results.
    private static ILlmProvider JoiningLlm()
    {
        var llm = new Mock<ILlmProvider>();
        llm
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = "SELECT 1 AS joined" });

        return llm.Object;
    }

    private static RoutedSource Routed()
    {
        return new RoutedSource { DataSourceId = DataSourceId, DataSourceName = "warehouse", Reason = "orders" };
    }

    private static CrossSourceQueryService CrossSource(Mock<IDataSourceProvider> provider, Mock<ISqlGenerationService> generation)
    {
        var knowledge = new Mock<IKnowledgeGraphService>();
        knowledge
            .Setup(x => x.GetSmartContextForAskAsync(DataSourceId, ProjectId, It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SmartSchemaContext { FullContext = "schema", DatabaseDialect = "PostgreSQL", ConfiguredDatabase = Configured });

        return new CrossSourceQueryService(
            Store().Factory(),
            Providers(provider.Object),
            new QueryGuardrailService(),
            TestSqlGate.Create(),
            TestConfiguredDatabases.Of(Configured),
            TestRowSecurity.Resolver,
            knowledge.Object,
            generation.Object,
            TestRowDataEgress.Discarding(),
            Options.Create(new McpDeploymentOptions()),
            new McpStatementLog(),
            TestQueryCostLimit.Passing,
            NullLoggerFactory.Instance,
            NullLogger<CrossSourceQueryService>.Instance);
    }

    private QueryService QueryService()
    {
        return TestQueryServices.QueryService(Store().Factory(), Connections(), configuredDatabases: new ConfiguredDatabaseResolver(Connections(), NullLogger<ConfiguredDatabaseResolver>.Instance));
    }

    private static DatabaseMetadata Table(string name)
    {
        var table = new DatabaseMetadata { DataSourceId = DataSourceId, SchemaName = "public", TableName = name };
        table.Columns = [new ColumnMetadata { ColumnName = "status", DataType = "text", DatabaseMetadata = table }];

        return table;
    }

    private static KnowledgeGraphService KnowledgeGraph(List<DatabaseMetadata> tables)
    {
        var dataSource = Source();
        foreach (var table in tables)
        {
            table.DataSource = dataSource;
        }

        var settings = new McpSettingsData
        {
            EnableLearning = false,
            EnableGoldenExemplars = false,
            EnableSemanticRetrieval = false,
            EnableValueGrounding = false
        };
        var settingsProvider = SettingsProviderMock.Create(settings);
        var store = Store(tables);

        return new KnowledgeGraphService(
            store.Factory(),
            settingsProvider.Object,
            SettingsProviderMock.Unshared(settingsProvider.Object),
            Mock.Of<IBeaconEmbeddingService>(),
            Mock.Of<ISchemaGraphService>(x => x.GetGraphAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())
                == Task.FromResult(SchemaGraph.Build(Array.Empty<TableMetadataDto>(), Array.Empty<SchemaRelationshipEdge>()))),
            Mock.Of<IValueGroundingService>(),
            TestConfiguredDatabases.Of(Configured),
            new QueryGuardrailService(),
            NullLogger<KnowledgeGraphService>.Instance);
    }

    // The data source, linked to the project, and its tables.
    private static MemoryStore Store(List<DatabaseMetadata>? tables = null)
    {
        var saved = new List<object>();
        tables ??= [];

        return new MemoryStore(new Dictionary<Type, object>
        {
            [typeof(DataSource)] = RecordingBeaconContext.MemorySet([Source()], saved),
            [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet([new ProjectDataSource { ProjectId = ProjectId, DataSourceId = DataSourceId }], saved),
            [typeof(DatabaseMetadata)] = RecordingBeaconContext.MemorySet(tables, saved),
            [typeof(ColumnMetadata)] = RecordingBeaconContext.MemorySet(tables.SelectMany(x => x.Columns).ToList(), saved)
        }, saved);
    }

    private static ConcurrentDictionary<DatabaseEngineType, Func<string, DbConnection>> Registry()
    {
        return (ConcurrentDictionary<DatabaseEngineType, Func<string, DbConnection>>)typeof(DbConnectionFactory)
            .GetField("_factories", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
    }

    private sealed record MemoryStore(Dictionary<Type, object> Sets, List<object> Saved)
    {
        public IDbContextFactory<BeaconContext> Factory()
        {
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(Sets, Saved));

            return factory.Object;
        }
    }

    private sealed record Paging : ListRequest;

    private sealed class ConnectionReachedException : Exception;
}

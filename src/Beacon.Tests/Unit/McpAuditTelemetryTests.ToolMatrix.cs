using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Documentation;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.McpEval;
using Beacon.Core.HostDocs;
using Beacon.Core.Mcp;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.MCP.Discovery;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// The per-tool audit matrix (§1.7, §9.5): every built-in tool, called through the production call-tool filters (scope,
/// then audit), writes exactly one audit row on success, on a thrown error and on a refusal, with the fields pinned
/// here; a learning signal is recorded for the SQL-carrying tools only (<c>ask</c>, <c>query</c>, <c>dry_run</c>), on
/// the failure path too; and with the audit required, a call whose audit row could not be written fails. The rows are
/// observed through the real <see cref="McpAuditService"/> and <see cref="McpSignalService"/> over mocked contexts
/// (§4.7). The matrix holds whether a tool writes its own row or hands the call to <see cref="McpToolCallRecorder"/>.
/// </summary>
public partial class McpAuditTelemetryTests
{
    private const int MatrixProjectId = 42;
    private const int ForeignProjectId = 43;
    private const int MatrixDataSourceId = 7;
    private const int MatrixUserId = 5;
    private const int RateableSignalId = 31;
    private const int ForeignSignalId = 32;
    private const string MatrixSql = "SELECT id, name FROM customers";
    private const string MatrixQuestion = "What does the customers table hold?";
    private const string DependencyFailure = "the knowledge store is unavailable";

    private static readonly string[] SignalTools = ["ask", "query", "dry_run"];

    private static IEnumerable<string> BuiltInTools() => McpToolCatalog.Names;

    [TestCaseSource(nameof(BuiltInTools))]
    public async Task ToolMatrix_Success_WritesOneAuditRow(string tool)
    {
        var world = new ToolMatrixWorld();

        var result = await world.CallAsync(tool, ToolMatrixCase.Success);

        result.IsError.Should().NotBe(true, TextOf(result));
        var row = world.AuditRows.Should().ContainSingle().Subject;
        row.Tool.Should().Be(tool);
        row.UserId.Should().Be(MatrixUserId);
        row.ProjectId.Should().Be(MatrixProjectId);
        row.DataSourceId.Should().Be(ExpectedDataSource(tool));
        row.ResultRowCount.Should().Be(tool switch { "query" => 2, "search" => 1, _ => null });
        row.ErrorMessage.Should().BeNull();
        AssertSignals(world, tool, successful: true);
    }

    [TestCaseSource(nameof(BuiltInTools))]
    public async Task ToolMatrix_ThrownError_WritesOneAuditRowWithTheError(string tool)
    {
        var world = new ToolMatrixWorld();

        var result = await world.CallAsync(tool, ToolMatrixCase.Throws);

        result.IsError.Should().BeTrue();
        TextOf(result).Should().Be(DependencyFailure, "an InvalidOperationException's message is caller-safe");
        var row = world.AuditRows.Should().ContainSingle().Subject;
        row.Tool.Should().Be(tool);
        row.UserId.Should().Be(MatrixUserId);
        row.ProjectId.Should().Be(MatrixProjectId);
        row.DataSourceId.Should().Be(ExpectedDataSource(tool));
        row.ResultRowCount.Should().BeNull();
        row.ErrorMessage.Should().Be(DependencyFailure);
        AssertSignals(world, tool, successful: false);
    }

    [TestCaseSource(nameof(BuiltInTools))]
    public async Task ToolMatrix_ProjectRefusal_WritesOneAuditRowWithTheRefusal(string tool)
    {
        var world = new ToolMatrixWorld();

        var result = await world.CallAsync(tool, ToolMatrixCase.ForeignProject);

        result.IsError.Should().BeTrue();
        var expected = tool == "feedback"
            ? $"Query signal {ForeignSignalId} not found."
            : $"Access denied: you do not have access to project {ForeignProjectId}.";
        TextOf(result).Should().Be(expected);
        var row = world.AuditRows.Should().ContainSingle().Subject;
        row.Tool.Should().Be(tool);
        row.UserId.Should().Be(MatrixUserId);
        row.ProjectId.Should().BeNull("the refused project is never attributed");
        row.DataSourceId.Should().Be(tool is "dry_run" or "get_query_context" ? MatrixDataSourceId : null, "these tools record the data source the caller named");
        row.ResultRowCount.Should().BeNull();
        row.ErrorMessage.Should().Be(expected);
        AssertSignals(world, tool, successful: false);
    }

    [TestCaseSource(nameof(BuiltInTools))]
    public async Task ToolMatrix_ScopeRefusal_WritesOneAuditRowAndRunsNoTool(string tool)
    {
        var world = new ToolMatrixWorld();

        var result = await world.CallAsync(tool, ToolMatrixCase.Success, executeScope: false);

        result.IsError.Should().BeTrue();
        TextOf(result).Should().Be(McpScopeCallToolFilter.MissingScopeMessage);
        world.ToolRan.Should().BeFalse();
        var row = world.AuditRows.Should().ContainSingle().Subject;
        row.Tool.Should().Be(tool);
        row.UserId.Should().Be(MatrixUserId);
        row.ProjectId.Should().BeNull();
        row.ErrorMessage.Should().Be(McpScopeCallToolFilter.MissingScopeMessage);
        world.Signals.Should().BeEmpty("a refused call never reached a SQL-carrying tool");
    }

    [TestCaseSource(nameof(BuiltInTools))]
    public async Task ToolMatrix_AuditRequiredAndTheRowCannotBeWritten_TheCallFails(string tool)
    {
        var world = new ToolMatrixWorld(auditRequired: true, auditSaveThrows: true);

        var result = await world.CallAsync(tool, ToolMatrixCase.Success);

        world.ToolRan.Should().BeTrue("the tool and its audit ran; only the result is replaced");
        result.IsError.Should().BeTrue();
        TextOf(result).Should().Be(McpAuditCallToolFilter.WithheldMessage);
    }

    [TestCaseSource(nameof(BuiltInTools))]
    public async Task ToolMatrix_AuditRequiredAndTheRowIsWritten_TheResultPasses(string tool)
    {
        var world = new ToolMatrixWorld(auditRequired: true);

        var result = await world.CallAsync(tool, ToolMatrixCase.Success);

        result.IsError.Should().NotBe(true, TextOf(result));
        world.AuditRows.Should().ContainSingle();
    }

    [Test]
    public async Task ToolMatrix_DryRunSignal_IsNamedDryRun_SoPatternMiningLeavesItOut()
    {
        var world = new ToolMatrixWorld();

        await world.CallAsync("dry_run", ToolMatrixCase.Success);

        // McpLearningAggregationService mines the "ask" and "query" signals only; dry_run's question is SQL.
        var signal = world.Signals.Should().ContainSingle().Subject;
        signal.Tool.Should().Be("dry_run");
    }

    private static int? ExpectedDataSource(string tool) =>
        tool is "query" or "dry_run" or "get_query_context" ? MatrixDataSourceId : null;

    private static void AssertSignals(ToolMatrixWorld world, string tool, bool successful)
    {
        if (!SignalTools.Contains(tool))
        {
            world.Signals.Should().BeEmpty($"{tool} is audit-only (§9.5)");
            return;
        }

        var signal = world.Signals.Should().ContainSingle($"{tool} carries SQL and records one signal (§9.5)").Subject;
        signal.Tool.Should().Be(tool);
        signal.UserId.Should().Be(MatrixUserId);
        signal.IsSuccessful.Should().Be(successful);
    }

    private static string TextOf(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(x => x.Text));

    private enum ToolMatrixCase
    {
        Success,
        Throws,
        ForeignProject
    }

    /// <summary>
    /// The eight built-in tools wired the way <c>AddBeaconMcp</c> scopes them, over mocks, and called through the
    /// production scope and audit call-tool filters.
    /// </summary>
    private sealed class ToolMatrixWorld
    {
        private readonly bool _auditRequired;
        private readonly bool _auditSaveThrows;

        public ToolMatrixWorld(bool auditRequired = false, bool auditSaveThrows = false)
        {
            _auditRequired = auditRequired;
            _auditSaveThrows = auditSaveThrows;
        }

        public List<McpAuditLog> AuditRows { get; } = [];

        public List<McpQuerySignal> Signals { get; } = [];

        public bool ToolRan { get; private set; }

        public async Task<CallToolResult> CallAsync(string tool, ToolMatrixCase matrixCase, bool executeScope = true)
        {
            using var provider = BuildServices(matrixCase);
            using var scope = provider.CreateScope();

            McpRequestHandler<CallToolRequestParams, CallToolResult> handler = (request, cancellationToken) =>
            {
                ToolRan = true;

                return DispatchAsync(request.Services!, request.Params!.Name, matrixCase, cancellationToken);
            };

            // Composed as the SDK composes AddBeaconMcp's filters: the scope filter outside the audit filter.
            var pipeline = McpScopeCallToolFilter.Create()(McpAuditCallToolFilter.Create()(handler));
            var request = new RequestContext<CallToolRequestParams>(
                Mock.Of<McpServer>(),
                new JsonRpcRequest { Method = "tools/call" },
                new CallToolRequestParams { Name = tool })
            {
                Services = scope.ServiceProvider,
                User = Caller(executeScope)
            };

            return await pipeline(request, CancellationToken.None);
        }

        private static ClaimsPrincipal Caller(bool executeScope) =>
            new(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, MatrixUserId.ToString()),
                    new Claim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.McpCallerAuthMethod),
                    new Claim(McpCallerClaimTypes.Scope, executeScope ? "Execute" : "Read")
                ],
                "Bearer"));

        private static async ValueTask<CallToolResult> DispatchAsync(
            IServiceProvider services, string tool, ToolMatrixCase matrixCase, CancellationToken cancellationToken)
        {
            int? projectId = matrixCase == ToolMatrixCase.ForeignProject ? ForeignProjectId : MatrixProjectId;

            return tool switch
            {
                "get_context" => await services.GetRequiredService<GetContextTool>().ExecuteAsync(
                    project_id: projectId, format: "agents_md", cancellationToken: cancellationToken),
                "ask" => await services.GetRequiredService<ProjectAskTool>().ExecuteAsync(
                    question: MatrixQuestion, project_id: projectId, cancellationToken: cancellationToken),
                "query" => await services.GetRequiredService<ProjectQueryTool>().ExecuteAsync(
                    datasource_id: MatrixDataSourceId, sql: MatrixSql, project_id: projectId, cancellationToken: cancellationToken),
                "get_documentation" => await services.GetRequiredService<ProjectGetDocumentationTool>().ExecuteAsync(
                    project_id: projectId, cancellationToken: cancellationToken),
                "search" => await services.GetRequiredService<ProjectSearchTool>().ExecuteAsync(
                    query: "customers", project_id: projectId, cancellationToken: cancellationToken),
                "dry_run" => await services.GetRequiredService<DryRunTool>().ExecuteAsync(
                    datasource_id: MatrixDataSourceId, sql: MatrixSql, project_id: projectId, cancellationToken: cancellationToken),
                "get_query_context" => await services.GetRequiredService<GetQueryContextTool>().ExecuteAsync(
                    question: MatrixQuestion, datasource_id: MatrixDataSourceId, project_id: projectId, cancellationToken: cancellationToken),
                "feedback" => await services.GetRequiredService<FeedbackTool>().ExecuteAsync(
                    signal_id: matrixCase == ToolMatrixCase.ForeignProject ? ForeignSignalId : RateableSignalId,
                    verdict: "correct",
                    cancellationToken: cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "not a built-in tool")
            };
        }

        private ServiceProvider BuildServices(ToolMatrixCase matrixCase)
        {
            var throws = matrixCase == ToolMatrixCase.Throws;
            var failure = new InvalidOperationException(DependencyFailure);

            var toolFactory = new Mock<IDbContextFactory<BeaconContext>>();
            toolFactory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new ToolMatrixContext(Signals));
            var auditFactory = new Mock<IDbContextFactory<BeaconContext>>();
            auditFactory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new ToolMatrixContext(Signals, AuditRows, _auditSaveThrows));

            var settingsProvider = SettingsProviderMock.Create();
            var sharedSettings = SettingsProviderMock.Unshared(settingsProvider.Object);
            var deployment = Options.Create(new McpDeploymentOptions { Audit = new McpAuditOptions { Required = _auditRequired } });
            var guardrail = new QueryGuardrailService();

            var knowledgeGraph = new Mock<IKnowledgeGraphService>();
            knowledgeGraph
                .Setup(x => x.GetSchemaCatalogAsync(MatrixDataSourceId, MatrixProjectId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["customers"] = ["id", "name"]
                });
            var smartContext = knowledgeGraph
                .Setup(x => x.GetSmartContextForAskAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()));
            var search = knowledgeGraph
                .Setup(x => x.SearchProjectAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()));
            if (throws)
            {
                smartContext.ThrowsAsync(failure);
                search.ThrowsAsync(failure);
            }
            else
            {
                smartContext.ReturnsAsync(new SmartSchemaContext { FullContext = "customers(id, name)\n", DatabaseDialect = "PostgreSQL" });
                search.ReturnsAsync(
                [
                    new SearchResult { Type = "table", DataSourceId = MatrixDataSourceId, DataSourceName = "warehouse", SchemaName = "public", TableName = "customers", Relevance = 1.0 }
                ]);
            }

            var queryExecution = new Mock<IQueryExecutionService>();
            var validate = queryExecution.Setup(x => x.ValidateAsync(MatrixDataSourceId, It.IsAny<string>(), It.IsAny<CancellationToken>()));
            if (throws)
            {
                validate.ThrowsAsync(failure);
            }
            else
            {
                validate.ReturnsAsync(ProviderDryRunOutcome.Valid());
            }

            var dataSourceProvider = new Mock<IDataSourceProvider>();
            var execute = dataSourceProvider.Setup(x => x.ExecuteReadOnlyQueryAsync(
                It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()));
            if (throws)
            {
                execute.ThrowsAsync(failure);
            }
            else
            {
                execute.ReturnsAsync(new ProviderQueryResult
                {
                    Success = true,
                    Rows =
                    [
                        new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Ada" },
                        new Dictionary<string, object?> { ["id"] = 2, ["name"] = "Grace" }
                    ]
                });
            }

            var providerFactory = new Mock<IDataSourceProviderFactory>();
            providerFactory
                .Setup(x => x.GetProvider(It.IsAny<DataSourceType>()))
                .Returns(dataSourceProvider.Object);

            var brief = new Mock<IProjectBriefService>();
            var buildBrief = brief.Setup(x => x.BuildAgentsMdAsync(MatrixProjectId, It.IsAny<CancellationToken>()));
            if (throws)
            {
                buildBrief.ThrowsAsync(failure);
            }
            else
            {
                buildBrief.ReturnsAsync("# AGENTS.md\n");
            }

            var documentation = new Mock<IProjectDocumentationService>();
            var export = documentation.Setup(x => x.ExportLatestToMarkdownAsync(MatrixProjectId, It.IsAny<CancellationToken>()));
            if (throws)
            {
                export.ThrowsAsync(failure);
            }
            else
            {
                export.ReturnsAsync("# Project documentation\n");
            }

            var intent = new Mock<IIntentClassifier>();
            var classify = intent.Setup(x => x.ClassifyAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<CancellationToken>()));
            if (throws)
            {
                classify.ThrowsAsync(failure);
            }
            else
            {
                classify.ReturnsAsync(IntentClassification.Knowledge);
            }

            var knowledgeAnswers = new Mock<IKnowledgeAnswerService>();
            knowledgeAnswers
                .Setup(x => x.AnswerAsync(It.IsAny<ILlmProvider>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("The customers table holds one row per customer.");

            var mediator = new Mock<IMediator>();
            var recordFeedback = mediator.Setup(x => x.Send(It.IsAny<RecordQueryFeedbackCommand>(), It.IsAny<CancellationToken>()));
            if (throws)
            {
                recordFeedback.ThrowsAsync(failure);
            }
            else
            {
                recordFeedback.ReturnsAsync(new RecordQueryFeedbackResult(false, null));
            }

            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddSingleton(deployment);
            services.AddSingleton(Mock.Of<ILlmProvider>());
            services.AddScoped(_ => new McpProjectContext
            {
                UserId = MatrixUserId,
                AllowedProjectIds = [MatrixProjectId],
                QueryProjectIds = [MatrixProjectId]
            });
            services.AddScoped<IProjectContext>(x => x.GetRequiredService<McpProjectContext>());
            services.AddScoped<McpAuditOutcome>();
            services.AddScoped<IMcpStatementLog, McpStatementLog>();
            services.AddScoped(x => new McpAuditService(
                auditFactory.Object,
                SettingsProviderMock.Create().Object,
                new HttpContextAccessor(),
                deployment,
                NullLogger<McpAuditService>.Instance,
                x.GetRequiredService<McpAuditOutcome>(),
                Options.Create(new BeaconTelemetryOptions()),
                NullLoggerFactory.Instance,
                TestMcpAudit.ContentHasher,
                x.GetRequiredService<IMcpStatementLog>()));
            services.AddScoped(_ => new McpSignalService(toolFactory.Object, settingsProvider.Object, NullLogger<McpSignalService>.Instance));
            services.AddScoped<McpToolCallRecorder>();
            services.AddSingleton(new McpQueryConcurrencyLimiter());

            services.AddScoped(x => new GetContextTool(
                knowledgeGraph.Object,
                toolFactory.Object,
                brief.Object,
                x.GetRequiredService<IProjectContext>(),
                x.GetRequiredService<McpToolCallRecorder>()));
            services.AddScoped(x => new ProjectSearchTool(
                knowledgeGraph.Object,
                x.GetRequiredService<IProjectContext>(),
                x.GetRequiredService<McpToolCallRecorder>()));
            services.AddScoped(x => new ProjectGetDocumentationTool(
                knowledgeGraph.Object,
                documentation.Object,
                toolFactory.Object,
                x.GetRequiredService<IProjectContext>(),
                settingsProvider.Object,
                x.GetRequiredService<McpToolCallRecorder>()));
            services.AddScoped(x => new GetQueryContextTool(
                knowledgeGraph.Object,
                toolFactory.Object,
                x.GetRequiredService<IProjectContext>(),
                x.GetRequiredService<McpToolCallRecorder>(),
                TestRowDataEgress.Discarding()));
            services.AddScoped(x => new DryRunTool(
                toolFactory.Object,
                TestSqlGate.Create(guardrail),
                TestConfiguredDatabases.None,
                TestRowSecurity.Resolver,
                knowledgeGraph.Object,
                queryExecution.Object,
                sharedSettings,
                x.GetRequiredService<IProjectContext>(),
                x.GetRequiredService<McpToolCallRecorder>(),
                x.GetRequiredService<McpQueryConcurrencyLimiter>()));
            services.AddScoped(x => new ProjectQueryTool(
                toolFactory.Object,
                providerFactory.Object,
                guardrail,
                TestSqlGate.Create(guardrail),
                TestConfiguredDatabases.None,
                TestRowSecurity.Resolver,
                knowledgeGraph.Object,
                sharedSettings,
                x.GetRequiredService<IProjectContext>(),
                x.GetRequiredService<McpToolCallRecorder>(),
                x.GetRequiredService<McpQueryConcurrencyLimiter>(),
                TestQueryCostLimit.Passing,
                NullLogger<ProjectQueryTool>.Instance));
            services.AddScoped(x => new ProjectAskTool(
                knowledgeGraph.Object,
                settingsProvider.Object,
                sharedSettings,
                x,
                x.GetRequiredService<IProjectContext>(),
                x.GetRequiredService<McpToolCallRecorder>(),
                Mock.Of<IAskSqlPipeline>(),
                Mock.Of<IAskSqlExecutor>(),
                intent.Object,
                Mock.Of<IDataSourceRouter>(),
                knowledgeAnswers.Object,
                Mock.Of<ICrossSourceQueryService>(),
                deployment,
                x.GetRequiredService<McpQueryConcurrencyLimiter>(),
                TimeProvider.System,
                NullLogger<ProjectAskTool>.Instance));
            services.AddScoped(x => new FeedbackTool(
                toolFactory.Object,
                x.GetRequiredService<IProjectContext>(),
                x.GetRequiredService<McpToolCallRecorder>(),
                mediator.Object,
                TestProjectAccess.Evaluator(TestProjectAccess.Unrestricted)));

            return services.BuildServiceProvider();
        }
    }

    /// <summary>
    /// Serves the tools' reads (the warehouse data source in the project, two rated-signal candidates, every other set
    /// empty) over async-queryable doubles and records the audit rows and signals added — no database (§4.7).
    /// </summary>
    private sealed class ToolMatrixContext : BeaconContext
    {
        private static readonly DbContextOptions<ToolMatrixContext> ContextOptions =
            new DbContextOptionsBuilder<ToolMatrixContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(SqlCapture.NeverConnects)
                .Options;

        private readonly List<McpQuerySignal> _signals;
        private readonly List<McpAuditLog>? _auditRows;
        private readonly bool _saveThrows;
        private readonly Dictionary<Type, object> _sets = [];

        public ToolMatrixContext(List<McpQuerySignal> signals, List<McpAuditLog>? auditRows = null, bool saveThrows = false)
            : base(ContextOptions, "beacon")
        {
            _signals = signals;
            _auditRows = auditRows;
            _saveThrows = saveThrows;
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (!_sets.TryGetValue(typeof(TEntity), out var set))
            {
                set = BuildSet(Seed<TEntity>());
                _sets[typeof(TEntity)] = set;
            }

            return (DbSet<TEntity>)set;
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            _saveThrows ? throw new InvalidOperationException("audit sink unavailable") : Task.FromResult(0);

        private static DataSource Warehouse() =>
            new()
            {
                Id = MatrixDataSourceId,
                Name = "warehouse",
                DataSourceType = DataSourceType.Database,
                EncryptedConnectionData = "encrypted",
                DatabaseEngineType = DatabaseEngineType.PostgreSQL
            };

        private static List<TEntity> Seed<TEntity>() where TEntity : class
        {
            object seed = typeof(TEntity) switch
            {
                var x when x == typeof(DataSource) => new List<DataSource> { Warehouse() },
                var x when x == typeof(ProjectDataSource) => new List<ProjectDataSource>
                {
                    new() { ProjectId = MatrixProjectId, DataSourceId = MatrixDataSourceId, DataSource = Warehouse() }
                },
                var x when x == typeof(McpQuerySignal) => new List<McpQuerySignal>
                {
                    new() { Id = RateableSignalId, ProjectId = MatrixProjectId, Tool = "ask", Question = MatrixQuestion },
                    new() { Id = ForeignSignalId, ProjectId = ForeignProjectId, Tool = "ask", Question = MatrixQuestion }
                },
                _ => new List<TEntity>()
            };

            return (List<TEntity>)seed;
        }

        private DbSet<TEntity> BuildSet<TEntity>(List<TEntity> data) where TEntity : class
        {
            var queryable = data.AsQueryable();
            var set = new Mock<DbSet<TEntity>>();
            set.As<IAsyncEnumerable<TEntity>>()
                .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                .Returns(() => new TestAsyncEnumerator<TEntity>(queryable.GetEnumerator()));
            set.As<IQueryable<TEntity>>()
                .Setup(x => x.Provider)
                .Returns(new TestAsyncQueryProvider<TEntity>(queryable.Provider));
            set.As<IQueryable<TEntity>>().Setup(x => x.Expression).Returns(queryable.Expression);
            set.As<IQueryable<TEntity>>().Setup(x => x.ElementType).Returns(queryable.ElementType);
            set.As<IQueryable<TEntity>>().Setup(x => x.GetEnumerator()).Returns(() => queryable.GetEnumerator());
            set.Setup(x => x.Add(It.IsAny<TEntity>())).Callback<TEntity>(Record);

            return set.Object;
        }

        private void Record<TEntity>(TEntity entity) where TEntity : class
        {
            if (entity is McpAuditLog row && _auditRows != null)
            {
                _auditRows.Add(row);
            }

            if (entity is McpQuerySignal signal)
            {
                _signals.Add(signal);
            }
        }
    }
}

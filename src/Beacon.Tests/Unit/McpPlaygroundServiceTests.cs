using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Documentation;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Authorization;
using Beacon.Core.Mcp;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.McpEval;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Retention;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.MCP;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;
using Beacon.Tests.Common;
using GuardrailValidationResult = Beacon.Core.Services.Security.QueryValidationResult;

namespace Beacon.Tests.Unit;

/// <summary>
/// <see cref="McpPlaygroundService"/> dispatch mapping: each tool-name arm must resolve the right
/// tool from the scope and forward the playground arguments intact. The CONTRACT under test is the
/// dispatch (arm reached + arguments arrived), not the tools' internals — those have their own
/// fixtures (DryRunToolTests, GetQueryContextToolTests, ProjectSearchToolPagingTests, …). Tools are
/// wired into a real ServiceCollection the way the service resolves them (scoped, sharing the
/// scope's McpProjectContext exactly like ServiceConfiguration does); their dependencies are the
/// same mock/double shapes the per-tool fixtures use (§4.7 — no DB).
/// </summary>
[TestFixture]
public class McpPlaygroundServiceTests
{
    private const int ProjectId = 42;
    private const int DataSourceId = 7;
    private const string ValidSql = "SELECT id, name FROM customers";
    private const string Question = "How many orders were placed last week?";

    private Mock<IKnowledgeGraphService> _knowledgeGraph = null!;
    private Mock<IQueryExecutionService> _queryExecution = null!;
    private List<object> _recorded = null!;

    [SetUp]
    public void SetUp()
    {
        _recorded = [];
        _knowledgeGraph = new Mock<IKnowledgeGraphService>();
        _queryExecution = new Mock<IQueryExecutionService>();

        _knowledgeGraph
            .Setup(x => x.GetSchemaCatalogAsync(DataSourceId, ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["customers"] = ["id", "name", "email"]
            });

        _knowledgeGraph
            .Setup(x => x.GetSmartContextForAskAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SmartSchemaContext
            {
                FullContext = "## Relevant Tables (full schema)\n\norders(id, placed_time)\n",
                DatabaseDialect = "PostgreSQL"
            });

        _queryExecution
            .Setup(x => x.ValidateAsync(DataSourceId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderDryRunOutcome.Valid());
    }

    [Test]
    public async Task DryRunArm_DispatchesToDryRunTool_ForwardingDataSourceAndSql()
    {
        var service = new McpPlaygroundService(BuildServiceProvider(), new BeaconMcpOptions());

        var result = await service.ExecuteToolAsync(
            "dry_run",
            new Dictionary<string, object?> { ["datasource_id"] = DataSourceId, ["sql"] = ValidSql },
            ProjectId,
            CancellationToken.None);

        result.IsError.Should().BeFalse(result.Text);
        result.Text.Should().Contain("# Dry Run");
        result.Text.Should().Contain("VALID");
        _queryExecution.Verify(
            x => x.ValidateAsync(DataSourceId, ValidSql, It.IsAny<CancellationToken>()), Times.Once,
            "the dispatch arm must hand datasource_id and sql to the real DryRunTool pipeline");
    }

    [Test]
    public async Task GetQueryContextArm_DispatchesToGetQueryContextTool_ForwardingQuestionAndDataSource()
    {
        var service = new McpPlaygroundService(BuildServiceProvider(), new BeaconMcpOptions());

        var result = await service.ExecuteToolAsync(
            "get_query_context",
            new Dictionary<string, object?> { ["question"] = Question, ["datasource_id"] = DataSourceId },
            ProjectId,
            CancellationToken.None);

        result.IsError.Should().BeFalse(result.Text);
        result.Text.Should().StartWith("# Query Context: warehouse (PostgreSQL)");
        _knowledgeGraph.Verify(
            x => x.GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once,
            "the dispatch arm must hand question and datasource_id to the real GetQueryContextTool");
    }

    [Test]
    public async Task SearchArm_ForwardsOffset()
    {
        _knowledgeGraph
            .Setup(x => x.SearchProjectAsync("customer", ProjectId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSearchResults(10));
        var service = new McpPlaygroundService(BuildServiceProvider(), new BeaconMcpOptions());

        var result = await service.ExecuteToolAsync(
            "search",
            new Dictionary<string, object?> { ["query"] = "customer", ["max_results"] = 2, ["offset"] = 3 },
            ProjectId,
            CancellationToken.None);

        result.IsError.Should().BeFalse(result.Text);
        result.Text.Should().Contain("(offset 3)", "the offset must reach the tool and shape the page");
        _knowledgeGraph.Verify(
            x => x.SearchProjectAsync("customer", ProjectId, 3 + 2 + 1, It.IsAny<CancellationToken>()), Times.Once,
            "the tool over-fetches offset + max_results + 1, which pins both values arriving intact");
    }

    [Test]
    public async Task GetDocumentationArm_ForwardsResponseFormat()
    {
        var service = new McpPlaygroundService(BuildServiceProvider(), new BeaconMcpOptions());

        var result = await service.ExecuteToolAsync(
            "get_documentation",
            new Dictionary<string, object?> { ["response_format"] = "bogus" },
            ProjectId,
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Text.Should().Be("response_format must be 'concise' or 'detailed'.",
            "this validation error is produced INSIDE the tool, proving response_format was forwarded");
    }

    [Test]
    public async Task AuditRequiredAndSaveFails_WithholdsTheResult_AfterTheToolRan()
    {
        var service = new McpPlaygroundService(BuildServiceProvider(auditRequired: true, auditSaveThrows: true), new BeaconMcpOptions());

        var result = await service.ExecuteToolAsync(
            "dry_run",
            new Dictionary<string, object?> { ["datasource_id"] = DataSourceId, ["sql"] = ValidSql },
            ProjectId,
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Text.Should().Be(McpAuditCallToolFilter.WithheldMessage);
        result.Text.Should().NotContain("VALID");
        _queryExecution.Verify(
            x => x.ValidateAsync(DataSourceId, ValidSql, It.IsAny<CancellationToken>()), Times.Once,
            "the withhold replaces the result after execution; the tool and its audit still ran");
    }

    [Test]
    public async Task AuditRequiredAndSaveSucceeds_ReturnsTheResult()
    {
        var service = new McpPlaygroundService(BuildServiceProvider(auditRequired: true), new BeaconMcpOptions());

        var result = await service.ExecuteToolAsync(
            "dry_run",
            new Dictionary<string, object?> { ["datasource_id"] = DataSourceId, ["sql"] = ValidSql },
            ProjectId,
            CancellationToken.None);

        result.IsError.Should().BeFalse(result.Text);
        result.Text.Should().Contain("VALID");
    }

    [Test]
    public async Task AuditNotRequiredAndSaveFails_ReturnsTheResult()
    {
        var service = new McpPlaygroundService(BuildServiceProvider(auditRequired: false, auditSaveThrows: true), new BeaconMcpOptions());

        var result = await service.ExecuteToolAsync(
            "dry_run",
            new Dictionary<string, object?> { ["datasource_id"] = DataSourceId, ["sql"] = ValidSql },
            ProjectId,
            CancellationToken.None);

        result.IsError.Should().BeFalse(result.Text);
        result.Text.Should().Contain("VALID");
    }

    [Test]
    public async Task NoRequest_IsDenied_BeforeAnyTool()
    {
        var service = new McpPlaygroundService(BuildServiceProvider(withRequest: false), new BeaconMcpOptions());

        var result = await service.ExecuteToolAsync(
            "dry_run",
            new Dictionary<string, object?> { ["datasource_id"] = DataSourceId, ["sql"] = ValidSql },
            ProjectId,
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Text.Should().Be(McpPlaygroundService.NoRequestMessage);
        _queryExecution.Verify(
            x => x.ValidateAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "work with no request is unrestricted, so the playground refuses it outright");
        _recorded.OfType<McpAuditLog>().Should().ContainSingle("the refusal is a tool invocation and is audited")
            .Which.Should().Match<McpAuditLog>(x => x.Tool == "dry_run" && x.UserId == null && x.ErrorMessage != null);
    }

    [Test]
    public async Task ARefusal_IsAudited_EvenWhenTheRequestIsCancelled()
    {
        var service = new McpPlaygroundService(BuildServiceProvider(withRequest: false), new BeaconMcpOptions());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await service.ExecuteToolAsync(
            "dry_run",
            new Dictionary<string, object?> { ["datasource_id"] = DataSourceId, ["sql"] = ValidSql },
            ProjectId,
            cancelled.Token);

        result.Text.Should().Be(McpPlaygroundService.NoRequestMessage);
        _recorded.OfType<McpAuditLog>().Should().ContainSingle("a decided refusal is written whatever the browser does");
    }

    [Test]
    public async Task Playground_UsesTheCallersProjectAccess_NonMemberOfARestrictedProjectIsDenied()
    {
        // A member of another project only: the restricted project is not in the caller's scope.
        var access = TestProjectAccess.Scope(new Dictionary<int, ProjectRole> { [ProjectId + 1] = ProjectRole.Editor }, includesOpen: false, notOpen: []);
        var service = new McpPlaygroundService(BuildServiceProvider(access: access), new BeaconMcpOptions());

        var result = await service.ExecuteToolAsync(
            "get_documentation", new Dictionary<string, object?>(), ProjectId, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Text.Should().Be($"Access denied: you do not have access to project {ProjectId}.");
        _recorded.OfType<McpAuditLog>().Should().ContainSingle("the refusal is a tool invocation and is audited")
            .Which.Should().Match<McpAuditLog>(x => x.Tool == "get_documentation" && x.UserId == 5 && x.ErrorMessage != null);
    }

    [Test]
    public async Task ATool_TheHostLeftOut_IsRefusedAsUnknown_AndAudited()
    {
        var options = new BeaconMcpOptions();
        options.BuiltInTools.Remove("query");
        var service = new McpPlaygroundService(BuildServiceProvider(), options);

        var result = await service.ExecuteToolAsync(
            "query", new Dictionary<string, object?> { ["sql"] = "SELECT 1" }, ProjectId, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Text.Should().Be("Unknown tool: query");
        _recorded.OfType<McpAuditLog>().Should().ContainSingle("the refusal is a tool invocation and is audited")
            .Which.Should().Match<McpAuditLog>(x => x.Tool == "query" && x.ErrorMessage != null);
    }

    [Test]
    public async Task Viewer_ReadsTheCatalog_ButCannotRunSqlCarryingTools()
    {
        var access = TestProjectAccess.Scope(new Dictionary<int, ProjectRole> { [ProjectId] = ProjectRole.Viewer }, includesOpen: false, notOpen: []);
        var service = new McpPlaygroundService(BuildServiceProvider(access: access), new BeaconMcpOptions());

        var dryRun = await service.ExecuteToolAsync(
            "dry_run",
            new Dictionary<string, object?> { ["datasource_id"] = DataSourceId, ["sql"] = ValidSql },
            ProjectId,
            CancellationToken.None);
        var documentation = await service.ExecuteToolAsync(
            "get_documentation", new Dictionary<string, object?> { ["response_format"] = "bogus" }, ProjectId, CancellationToken.None);

        dryRun.Text.Should().Be($"Access denied: you do not have access to project {ProjectId}.", "dry_run needs Query, a Viewer has Read and Run");
        _queryExecution.Verify(
            x => x.ValidateAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        documentation.Text.Should().Be("response_format must be 'concise' or 'detailed'.", "the catalog tool ran: a Viewer reads the project");
    }

    // get_query_context needs Read only; live value probes need Query, so a Viewer gets stored sample values alone.
    [TestCase(ProjectRole.Viewer, false)]
    [TestCase(ProjectRole.Editor, true)]
    public async Task GetQueryContext_ProbesLiveValues_OnlyForACallerWhoMayQueryTheProject(ProjectRole role, bool liveValueProbes)
    {
        var access = TestProjectAccess.Scope(new Dictionary<int, ProjectRole> { [ProjectId] = role }, includesOpen: false, notOpen: []);
        var service = new McpPlaygroundService(BuildServiceProvider(access: access), new BeaconMcpOptions());

        var result = await service.ExecuteToolAsync(
            "get_query_context",
            new Dictionary<string, object?> { ["question"] = Question, ["datasource_id"] = DataSourceId },
            ProjectId,
            CancellationToken.None);

        result.IsError.Should().BeFalse(result.Text);
        _knowledgeGraph.Verify(
            x => x.GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestCase("ask", true)]
    [TestCase("query", true)]
    [TestCase("dry_run", true)]
    [TestCase("feedback", true)]
    [TestCase("search", false)]
    [TestCase("get_context", false)]
    [TestCase("get_query_context", false)]
    [TestCase("get_documentation", false)]
    public async Task AViewer_IsDeniedTheToolsThatQuery_AndRunsTheCatalogTools(string tool, bool needsQuery)
    {
        var access = TestProjectAccess.Scope(new Dictionary<int, ProjectRole> { [ProjectId] = ProjectRole.Viewer }, includesOpen: false, notOpen: []);
        var service = new McpPlaygroundService(BuildServiceProvider(access: access), new BeaconMcpOptions());

        var result = await service.ExecuteToolAsync(tool, new Dictionary<string, object?>(), ProjectId, CancellationToken.None);

        McpToolProjectAction.Of(tool).Should().Be(needsQuery ? ProjectAction.Query : ProjectAction.Read);
        if (needsQuery)
        {
            result.IsError.Should().BeTrue();
            result.Text.Should().Be($"Access denied: you do not have access to project {ProjectId}.");
        }
        else
        {
            result.Text.Should().NotStartWith("Access denied", "a Viewer reads the project's catalog");
        }
    }

    [Test]
    public async Task GetDocumentationArm_ReadsTableKnowledgeAndLineage_ForThePinnedProjectOnly()
    {
        _knowledgeGraph
            .Setup(x => x.GetTableKnowledgeAsync(DataSourceId, ProjectId, "public", "customers", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TableKnowledge
            {
                DataSourceId = DataSourceId,
                DataSourceName = "warehouse",
                SchemaName = "public",
                TableName = "customers",
                Description = "Customers of the pinned project"
            });
        _knowledgeGraph
            .Setup(x => x.GetLineageAsync(DataSourceId, ProjectId, "public", "customers", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LineageInfo { SchemaName = "public", TableName = "customers" });
        var service = new McpPlaygroundService(BuildServiceProvider(), new BeaconMcpOptions());

        var result = await service.ExecuteToolAsync(
            "get_documentation",
            new Dictionary<string, object?> { ["datasource_name"] = "warehouse", ["table_name"] = "customers" },
            ProjectId,
            CancellationToken.None);

        result.IsError.Should().BeFalse(result.Text);
        result.Text.Should().Contain("Customers of the pinned project");
        _knowledgeGraph.Verify(
            x => x.GetTableKnowledgeAsync(It.IsAny<int>(), It.Is<int>(y => y != ProjectId), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "documentation and code references are read for the pinned project, never another one sharing the source");
        _knowledgeGraph.Verify(
            x => x.GetLineageAsync(DataSourceId, ProjectId, "public", "customers", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ASignedInSessionsSignalAndAuditRow_NameItsBeaconUser_NotItsSessionId()
    {
        // A cookie session's NameIdentifier is its user's external id; the signal and the audit row record its Users.Id.
        var session = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "8d6f0c2e-ext")], "Cookies"));
        var service = new McpPlaygroundService(BuildServiceProvider(caller: session, actors: TestProjectAccess.Actors(beaconUserId: 12, externalId: "8d6f0c2e-ext")), new BeaconMcpOptions());

        var result = await service.ExecuteToolAsync(
            "dry_run",
            new Dictionary<string, object?> { ["datasource_id"] = DataSourceId, ["sql"] = ValidSql },
            ProjectId,
            CancellationToken.None);

        result.IsError.Should().BeFalse(result.Text);
        _recorded.OfType<McpQuerySignal>().Should().ContainSingle().Which.UserId.Should().Be(12);
        _recorded.OfType<McpAuditLog>().Should().ContainSingle().Which.UserId.Should().Be(12);
    }

    [Test]
    public async Task ASignalRecordedForASignedInSession_CanBeRatedByThatUser()
    {
        var session = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "8d6f0c2e-ext")], "Cookies"));
        var actors = TestProjectAccess.Actors(beaconUserId: 12, externalId: "8d6f0c2e-ext");
        var service = new McpPlaygroundService(BuildServiceProvider(caller: session, actors: actors), new BeaconMcpOptions());
        await service.ExecuteToolAsync(
            "dry_run",
            new Dictionary<string, object?> { ["datasource_id"] = DataSourceId, ["sql"] = ValidSql },
            ProjectId,
            CancellationToken.None);
        var signal = _recorded.OfType<McpQuerySignal>().Single();
        signal.Id = 31;
        signal.ProjectId = ProjectId;
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object>
                {
                    [typeof(McpQuerySignal)] = RecordingBeaconContext.MemorySet(new List<McpQuerySignal> { signal }, []),
                    [typeof(McpEvalCase)] = RecordingBeaconContext.MemorySet(new List<McpEvalCase>(), [])
                },
                []));
        var policy = new Mock<IContentRetentionPolicy>();
        policy
            .Setup(x => x.ResolveAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentRetentionDecision(true, true));
        // An Editor in the project, not its project admin: only the own-caller rule lets it rate the signal.
        var editor = TestProjectAccess.Scope(new Dictionary<int, ProjectRole> { [ProjectId] = ProjectRole.Editor }, includesOpen: false, notOpen: []);
        var handler = new RecordQueryFeedbackHandler(
            factory.Object,
            TestGroundingSqlChecks.PassThrough().Object,
            policy.Object,
            TestProjectAccess.Evaluator(editor),
            actors,
            NullLogger<RecordQueryFeedbackHandler>.Instance);

        var act = () => handler.Handle(new RecordQueryFeedbackCommand(31, McpUserVerdict.Incorrect), CancellationToken.None);

        await act.Should().NotThrowAsync();
        signal.UserVerdict.Should().Be(McpUserVerdict.Incorrect);
    }

    [Test]
    public async Task UnknownTool_ReturnsError()
    {
        var service = new McpPlaygroundService(BuildServiceProvider(), new BeaconMcpOptions());

        var result = await service.ExecuteToolAsync(
            "definitely_not_a_tool", new Dictionary<string, object?>(), ProjectId, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Text.Should().Be("Unknown tool: definitely_not_a_tool");
    }

    private IServiceProvider BuildServiceProvider(
        bool auditRequired = false,
        bool auditSaveThrows = false,
        bool withRequest = true,
        ProjectAccessScope? access = null,
        ClaimsPrincipal? caller = null,
        IBeaconActorAccessor? actors = null)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new PlaygroundTestContext(_recorded));

        // The audit rows go through their own factory so a failing audit save leaves the tool's reads intact.
        var auditFactory = new Mock<IDbContextFactory<BeaconContext>>();
        auditFactory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(x => x.IsCancellationRequested
                ? Task.FromCanceled<BeaconContext>(x)
                : Task.FromResult<BeaconContext>(new PlaygroundTestContext(_recorded, auditSaveThrows)));
        var deploymentOptions = Options.Create(new McpDeploymentOptions { Audit = new McpAuditOptions { Required = auditRequired } });

        var settingsProvider = SettingsProviderMock.Create();
        settingsProvider
            .Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpSettingsData());

        var guardrail = new Mock<IQueryGuardrailService>();
        guardrail
            .Setup(x => x.ValidateQuery(It.IsAny<string>(), It.IsAny<QueryGuardrailOptions?>()))
            .Returns(new GuardrailValidationResult(true));

        var services = new ServiceCollection();

        // The playground mutates the SCOPED McpProjectContext; the tools must observe the same
        // instance, so IProjectContext maps onto it exactly like ServiceConfiguration's factory
        // does on the playground path. A signed-in request (unless withRequest is false) whose
        // project access is the given scope (default: an Admin's).
        services.AddScoped<McpProjectContext>();
        services.AddScoped<IProjectContext>(x => x.GetRequiredService<McpProjectContext>());
        var request = withRequest
            ? new DefaultHttpContext { User = caller ?? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "5")], "Cookies")) }
            : null;
        services.AddSingleton(Mock.Of<IHttpContextAccessor>(x => x.HttpContext == request));
        services.AddSingleton(TestProjectAccess.Evaluator(access ?? TestProjectAccess.Unrestricted));
        services.AddSingleton(actors ?? TestProjectAccess.Actors(beaconUserId: 5, externalId: "5"));
        // Same shape as ServiceConfiguration: one scoped McpAuditOutcome per playground scope, shared by the audit
        // service and the playground's withhold decision.
        services.AddSingleton(deploymentOptions);
        services.AddScoped<McpAuditOutcome>();
        services.AddScoped(x => new McpAuditService(auditFactory.Object, SettingsProviderMock.Create().Object, new HttpContextAccessor(), deploymentOptions, NullLogger<McpAuditService>.Instance, x.GetRequiredService<McpAuditOutcome>(), Options.Create(new BeaconTelemetryOptions()), NullLoggerFactory.Instance, TestMcpAudit.ContentHasher, new McpStatementLog()));
        // The scope's call record, as ServiceConfiguration registers it: the playground's refusals are written from it.
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        services.AddScoped(_ => new McpSignalService(factory.Object, settingsProvider.Object, NullLogger<McpSignalService>.Instance));
        services.AddScoped<McpToolCallRecorder>();

        services.AddScoped(x => new DryRunTool(
            factory.Object,
            TestSqlGate.Create(guardrail.Object),
            TestConfiguredDatabases.None,
            TestRowSecurity.Resolver,
            _knowledgeGraph.Object,
            _queryExecution.Object,
            SettingsProviderMock.Unshared(settingsProvider.Object),
            x.GetRequiredService<IProjectContext>(),
            x.GetRequiredService<McpToolCallRecorder>(),
            new McpQueryConcurrencyLimiter()));

        services.AddScoped(x => new GetQueryContextTool(
            _knowledgeGraph.Object,
            factory.Object,
            x.GetRequiredService<IProjectContext>(),
            x.GetRequiredService<McpToolCallRecorder>(),
            TestRowDataEgress.Discarding()));

        services.AddScoped(x => new ProjectSearchTool(
            _knowledgeGraph.Object,
            x.GetRequiredService<IProjectContext>(),
            x.GetRequiredService<McpToolCallRecorder>()));

        services.AddScoped(x => new ProjectGetDocumentationTool(
            _knowledgeGraph.Object,
            Mock.Of<IProjectDocumentationService>(),
            factory.Object,
            x.GetRequiredService<IProjectContext>(),
            x.GetRequiredService<McpToolCallRecorder>()));

        return services.BuildServiceProvider();
    }

    private static List<SearchResult> MakeSearchResults(int count)
    {
        return Enumerable.Range(1, count)
            .Select(x =>
                new SearchResult
                {
                    Type = "table",
                    DataSourceId = DataSourceId,
                    DataSourceName = "warehouse",
                    SchemaName = "public",
                    TableName = $"table_{x:00}",
                    Relevance = 1.0 - (x * 0.01)
                })
            .ToList();
    }

    /// <summary>Serves the data-source resolution queries over async-queryable doubles and records the audit rows and
    /// signals added — no DB, no UseInMemoryDatabase (§4.7). Mirrors DryRunToolTests.</summary>
    private sealed class PlaygroundTestContext : BeaconContext
    {
        // A set it does not serve never connects: reading one fails at once (SqlCapture.NeverConnects).
        private static readonly DbContextOptions<PlaygroundTestContext> Options =
            new DbContextOptionsBuilder<PlaygroundTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(SqlCapture.NeverConnects)
                .Options;

        private readonly Mock<DbSet<McpAuditLog>> _auditSet = new();
        private readonly Mock<DbSet<McpQuerySignal>> _signalSet = new();
        private readonly bool _saveThrows;

        public PlaygroundTestContext(List<object> recorded, bool saveThrows = false) : base(Options, "beacon")
        {
            _saveThrows = saveThrows;
            _auditSet.Setup(x => x.Add(It.IsAny<McpAuditLog>())).Callback<McpAuditLog>(recorded.Add);
            _signalSet.Setup(x => x.Add(It.IsAny<McpQuerySignal>())).Callback<McpQuerySignal>(recorded.Add);
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpAuditLog))
            {
                return (DbSet<TEntity>)(object)_auditSet.Object;
            }

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
                        DataSourceType = DataSourceType.Database,
                        EncryptedConnectionData = "encrypted",
                        DatabaseEngineType = DatabaseEngineType.PostgreSQL
                    }
                });
            }

            if (typeof(TEntity) == typeof(ProjectDataSource))
            {
                return (DbSet<TEntity>)(object)BuildSet(new List<ProjectDataSource>
                {
                    new() { ProjectId = ProjectId, DataSourceId = DataSourceId, DataSource = new DataSource { Id = DataSourceId, Name = "warehouse", DataSourceType = DataSourceType.Database, EncryptedConnectionData = "encrypted" } }
                });
            }

            return base.Set<TEntity>();
        }

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            _saveThrows ? throw new InvalidOperationException("audit sink unavailable") : Task.FromResult(0);

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

using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Documentation;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
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

    [SetUp]
    public void SetUp()
    {
        _knowledgeGraph = new Mock<IKnowledgeGraphService>();
        _queryExecution = new Mock<IQueryExecutionService>();

        _knowledgeGraph
            .Setup(x => x.GetSchemaCatalogAsync(DataSourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["customers"] = ["id", "name", "email"]
            });

        _knowledgeGraph
            .Setup(x => x.GetSmartContextForAskAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        var service = new McpPlaygroundService(BuildServiceProvider());

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
        var service = new McpPlaygroundService(BuildServiceProvider());

        var result = await service.ExecuteToolAsync(
            "get_query_context",
            new Dictionary<string, object?> { ["question"] = Question, ["datasource_id"] = DataSourceId },
            ProjectId,
            CancellationToken.None);

        result.IsError.Should().BeFalse(result.Text);
        result.Text.Should().StartWith("# Query Context: warehouse (PostgreSQL)");
        _knowledgeGraph.Verify(
            x => x.GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, It.IsAny<CancellationToken>()), Times.Once,
            "the dispatch arm must hand question and datasource_id to the real GetQueryContextTool");
    }

    [Test]
    public async Task SearchArm_ForwardsOffset()
    {
        _knowledgeGraph
            .Setup(x => x.SearchProjectAsync("customer", ProjectId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSearchResults(10));
        var service = new McpPlaygroundService(BuildServiceProvider());

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
        var service = new McpPlaygroundService(BuildServiceProvider());

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
    public async Task UnknownTool_ReturnsError()
    {
        var service = new McpPlaygroundService(BuildServiceProvider());

        var result = await service.ExecuteToolAsync(
            "definitely_not_a_tool", new Dictionary<string, object?>(), ProjectId, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Text.Should().Be("Unknown tool: definitely_not_a_tool");
    }

    private IServiceProvider BuildServiceProvider()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new PlaygroundTestContext());

        var settingsProvider = new Mock<IMcpSettingsProvider>();
        settingsProvider
            .Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpSettingsData());

        var guardrail = new Mock<IQueryGuardrailService>();
        guardrail
            .Setup(x => x.ValidateQuery(It.IsAny<string>(), It.IsAny<QueryGuardrailOptions?>()))
            .Returns(new GuardrailValidationResult(true));
        guardrail
            .Setup(x => x.ApplyRowLimit(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>()))
            .Returns<string, int, string?>((sql, maxRows, _) => $"{sql} LIMIT {maxRows}");

        var services = new ServiceCollection();

        // The playground mutates the SCOPED McpProjectContext; the tools must observe the same
        // instance, so IProjectContext maps onto it exactly like ServiceConfiguration's factory
        // does on the playground path (no HttpContext: attribution is skipped and the API-key
        // project gate passes for non-API-key callers).
        services.AddScoped<McpProjectContext>();
        services.AddScoped<IProjectContext>(x => x.GetRequiredService<McpProjectContext>());
        services.AddSingleton(Mock.Of<IHttpContextAccessor>());
        services.AddScoped(x => new McpAuditService(factory.Object, NullLogger<McpAuditService>.Instance));

        services.AddScoped(x => new DryRunTool(
            factory.Object,
            guardrail.Object,
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            new SqlSchemaValidator(),
            _knowledgeGraph.Object,
            _queryExecution.Object,
            settingsProvider.Object,
            x.GetRequiredService<IProjectContext>(),
            x.GetRequiredService<McpAuditService>(),
            new McpSignalService(factory.Object, settingsProvider.Object, NullLogger<McpSignalService>.Instance),
            NullLogger<DryRunTool>.Instance));

        services.AddScoped(x => new GetQueryContextTool(
            _knowledgeGraph.Object,
            factory.Object,
            x.GetRequiredService<IProjectContext>(),
            x.GetRequiredService<McpAuditService>(),
            NullLogger<GetQueryContextTool>.Instance));

        services.AddScoped(x => new ProjectSearchTool(
            _knowledgeGraph.Object,
            x.GetRequiredService<IProjectContext>(),
            x.GetRequiredService<McpAuditService>(),
            NullLogger<ProjectSearchTool>.Instance));

        services.AddScoped(x => new ProjectGetDocumentationTool(
            _knowledgeGraph.Object,
            Mock.Of<IProjectDocumentationService>(),
            factory.Object,
            x.GetRequiredService<IProjectContext>(),
            x.GetRequiredService<McpAuditService>(),
            NullLogger<ProjectGetDocumentationTool>.Instance));

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

    /// <summary>Serves the data-source resolution queries over async-queryable doubles and accepts
    /// audit rows — no DB, no UseInMemoryDatabase (§4.7). Mirrors DryRunToolTests.</summary>
    private sealed class PlaygroundTestContext : BeaconContext
    {
        private static readonly DbContextOptions<PlaygroundTestContext> Options =
            new DbContextOptionsBuilder<PlaygroundTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly Mock<DbSet<McpAuditLog>> _auditSet = new();
        private readonly Mock<DbSet<McpQuerySignal>> _signalSet = new();

        public PlaygroundTestContext() : base(Options, "beacon")
        {
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

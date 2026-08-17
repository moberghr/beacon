using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// The <c>get_query_context</c> MCP tool: exposes the grounding context <c>ask</c> assembles
/// internally (<see cref="IKnowledgeGraphService.GetSmartContextForAskAsync"/>) so an agent can write
/// its own SQL. Verifies source resolution (single-source auto-resolve, multi-source "id: name"
/// listing, explicit name), the prepended header + usage hint, section-boundary truncation with an
/// explicit note, and the structured payload. Data-source resolution runs against async-queryable
/// doubles (§4.7), mirroring DryRunToolTests.
/// </summary>
[TestFixture]
public class GetQueryContextToolTests
{
    private const int ProjectId = 42;
    private const int WarehouseId = 7;
    private const int CrmId = 8;
    private const string Question = "How many orders were placed last week?";
    private const string FullContext =
        "# Data Source: warehouse (PostgreSQL)\nTables: 1\n\n" +
        "## Relevant Tables (full schema)\n\norders(id, placed_time)\n\n" +
        "## Join Paths (verified)\n- orders.customer_id -> customers.id\n";

    private Mock<IKnowledgeGraphService> _knowledgeGraph = null!;
    private List<McpAuditLog> _auditLogs = null!;

    [SetUp]
    public void SetUp()
    {
        _knowledgeGraph = new Mock<IKnowledgeGraphService>();
        _auditLogs = [];
        SetupSmartContext(FullContext);
    }

    [Test]
    public async Task SingleSource_AutoResolves_ReturnsHeaderContextAndStructuredPayload()
    {
        var result = await CreateTool(multiSource: false).ExecuteAsync(
            question: Question, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        var text = GetText(result);
        text.Should().StartWith("# Query Context: warehouse (PostgreSQL)");
        text.Should().Contain("Write PostgreSQL SELECT statements. Validate with dry_run, execute with query. Sections marked (authoritative) are human-verified.");
        text.Should().Contain(FullContext);
        text.Should().NotContain("_Truncated at");

        _knowledgeGraph.Verify(
            x => x.GetSmartContextForAskAsync(WarehouseId, ProjectId, Question, It.IsAny<CancellationToken>()), Times.Once,
            "the caller's authorized projectId must be threaded into grounding (codex PR-11 R4)");

        var structured = result.StructuredContent!.Value;
        structured.GetProperty("data_source_id").GetInt32().Should().Be(WarehouseId);
        structured.GetProperty("data_source").GetString().Should().Be("warehouse");
        structured.GetProperty("dialect").GetString().Should().Be("PostgreSQL");
        structured.GetProperty("truncated").GetBoolean().Should().BeFalse();

        _auditLogs.Should().ContainSingle("§1.7 — the success path must record an audit row");
        _auditLogs[0].Tool.Should().Be("get_query_context");
        _auditLogs[0].ErrorMessage.Should().BeNull();
        _auditLogs[0].DataSourceId.Should().Be(WarehouseId);
        _auditLogs[0].ProjectId.Should().Be(ProjectId);
    }

    [Test]
    public async Task MultiSource_WithoutSelector_ErrorsListingIdNamePairs()
    {
        var result = await CreateTool(multiSource: true).ExecuteAsync(
            question: Question, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeTrue();
        var text = GetText(result);
        text.Should().Contain("2 data sources");
        text.Should().Contain($"{WarehouseId}: warehouse");
        text.Should().Contain($"{CrmId}: crm");
        text.Should().Contain("Pass datasource_name or datasource_id");

        _knowledgeGraph.Verify(
            x => x.GetSmartContextForAskAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task ExplicitDataSourceName_Resolves()
    {
        var result = await CreateTool(multiSource: true).ExecuteAsync(
            question: Question, datasource_name: "crm", cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        GetText(result).Should().StartWith("# Query Context: crm (PostgreSQL)");

        _knowledgeGraph.Verify(
            x => x.GetSmartContextForAskAsync(CrmId, ProjectId, Question, It.IsAny<CancellationToken>()), Times.Once);

        var structured = result.StructuredContent!.Value;
        structured.GetProperty("data_source_id").GetInt32().Should().Be(CrmId);
        structured.GetProperty("data_source").GetString().Should().Be("crm");
    }

    [Test]
    public async Task ContextOverBudget_CutsAtLastSectionBoundary_AndAppendsNote()
    {
        var oversized =
            "# Data Source: warehouse (PostgreSQL)\n" +
            "## Section A\n" + new string('a', 900) + "\n" +
            "## Section B\n" + new string('b', 300) + "\n";
        SetupSmartContext(oversized);

        var result = await CreateTool(multiSource: false).ExecuteAsync(
            question: Question, max_chars: 1000, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        var text = GetText(result);
        text.Should().Contain("## Section A");
        text.Should().NotContain("## Section B", "the partially-cut trailing section must be dropped at the boundary");
        text.Should().Contain("_Truncated at 1000 chars. Raise max_chars (up to 30000) for the full context._");

        var structured = result.StructuredContent!.Value;
        structured.GetProperty("truncated").GetBoolean().Should().BeTrue();
    }

    [Test]
    public async Task SmallContext_NoTruncationNote_TruncatedFalse()
    {
        var result = await CreateTool(multiSource: false).ExecuteAsync(
            question: Question, max_chars: 30000, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        var text = GetText(result);
        text.Should().Contain(FullContext);
        text.Should().NotContain("_Truncated at");

        var structured = result.StructuredContent!.Value;
        structured.GetProperty("truncated").GetBoolean().Should().BeFalse();
    }

    [Test]
    public async Task EmptyQuestion_ReturnsError()
    {
        var result = await CreateTool(multiSource: false).ExecuteAsync(
            question: "", cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeTrue();
        GetText(result).Should().Be("Missing required parameter: question");

        _knowledgeGraph.Verify(
            x => x.GetSmartContextForAskAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        _auditLogs.Should().ContainSingle("§1.7 — early-exit errors must still record an audit row");
        _auditLogs[0].Tool.Should().Be("get_query_context");
        _auditLogs[0].ErrorMessage.Should().Be("Missing required parameter: question");
    }

    [Test]
    public async Task SmartContextThrows_CatchPathAudits_WithResolvedProjectId()
    {
        // Mirrors DryRunToolTests.SchemaCatalogThrows: the catch path must still record an audit
        // row (§1.7) carrying the resolved project + data source and the raw error.
        _knowledgeGraph
            .Setup(x => x.GetSmartContextForAskAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("knowledge graph offline"));

        var result = await CreateTool(multiSource: false).ExecuteAsync(
            question: Question, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeTrue();
        _auditLogs.Should().ContainSingle("§1.7 — the catch path must record an audit row");
        _auditLogs[0].Tool.Should().Be("get_query_context");
        _auditLogs[0].ErrorMessage.Should().Be("knowledge graph offline");
        _auditLogs[0].ProjectId.Should().Be(ProjectId,
            "the catch path passes the resolved projectId raw, matching FailAsync");
        _auditLogs[0].DataSourceId.Should().Be(WarehouseId,
            "the data source was already resolved when the context assembly threw");
    }

    [Test]
    public async Task HardCutTruncation_NeverSplitsASurrogatePair()
    {
        // No newline in the first 1000 chars, so TrimToBudget falls through the section and line
        // boundaries to the hard cut, which lands between the halves of the emoji's surrogate pair
        // (999 'a's + high surrogate at index 999, low surrogate at index 1000).
        var oversized = new string('a', 999) + "\U0001F600" + new string('b', 200);
        SetupSmartContext(oversized);

        var result = await CreateTool(multiSource: false).ExecuteAsync(
            question: Question, max_chars: 1000, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        var text = GetText(result);
        text.Should().Contain(new string('a', 999));
        text.Should().NotContain("\U0001F600");
        text.Should().NotContain("\uD83D", "the dangling high surrogate must be dropped with its pair");

        var structured = result.StructuredContent!.Value;
        structured.GetProperty("truncated").GetBoolean().Should().BeTrue();
    }

    private void SetupSmartContext(string fullContext)
    {
        _knowledgeGraph
            .Setup(x => x.GetSmartContextForAskAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SmartSchemaContext
            {
                FullContext = fullContext,
                DatabaseDialect = "PostgreSQL"
            });
    }

    private GetQueryContextTool CreateTool(bool multiSource)
    {
        // One factory serves both the tool (data-source resolution) and the audit service; each
        // CreateDbContextAsync call gets a fresh context over the same captured audit-log list
        // (§4.7 — async-queryable doubles, no DB). Mirrors FeedbackToolTests.
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new QueryContextTestContext(multiSource, _auditLogs));

        var projectContext = new McpProjectContext { UserId = 1, AllowedProjectIds = [ProjectId] };

        var auditService = new McpAuditService(factory.Object, NullLogger<McpAuditService>.Instance);

        return new GetQueryContextTool(
            _knowledgeGraph.Object,
            factory.Object,
            projectContext,
            auditService,
            NullLogger<GetQueryContextTool>.Instance);
    }

    private static string GetText(CallToolResult result)
    {
        return result.Content.OfType<TextContentBlock>().Single().Text;
    }

    /// <summary>Serves the data-source resolution queries (ProjectDataSources membership/name lookup +
    /// DataSources name projection) over async-queryable doubles — no DB, no UseInMemoryDatabase (§4.7).
    /// The ProjectDataSource rows carry their DataSource navigation so name resolution works in-memory.
    /// Also captures the McpAuditLog rows the real McpAuditService writes, mirroring FeedbackToolTests.</summary>
    private sealed class QueryContextTestContext : BeaconContext
    {
        private static readonly DbContextOptions<QueryContextTestContext> Options =
            new DbContextOptionsBuilder<QueryContextTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly List<DataSource> _dataSources;
        private readonly List<ProjectDataSource> _projectDataSources;
        private readonly Mock<DbSet<McpAuditLog>> _auditSet = new();

        public QueryContextTestContext(bool multiSource, List<McpAuditLog> auditLogs) : base(Options, "beacon")
        {
            _auditSet.Setup(x => x.Add(It.IsAny<McpAuditLog>()))
                .Callback<McpAuditLog>(auditLogs.Add);

            _dataSources =
            [
                new DataSource
                {
                    Id = WarehouseId,
                    Name = "warehouse",
                    DataSourceType = DataSourceType.Database,
                    EncryptedConnectionData = "encrypted",
                    DatabaseEngineType = DatabaseEngineType.PostgreSQL
                }
            ];

            if (multiSource)
            {
                _dataSources.Add(new DataSource
                {
                    Id = CrmId,
                    Name = "crm",
                    DataSourceType = DataSourceType.Database,
                    EncryptedConnectionData = "encrypted",
                    DatabaseEngineType = DatabaseEngineType.PostgreSQL
                });
            }

            _projectDataSources = _dataSources
                .Select(x =>
                    new ProjectDataSource
                    {
                        ProjectId = ProjectId,
                        DataSourceId = x.Id,
                        DataSource = x
                    })
                .ToList();
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpAuditLog))
            {
                return (DbSet<TEntity>)(object)_auditSet.Object;
            }

            if (typeof(TEntity) == typeof(DataSource))
            {
                return (DbSet<TEntity>)(object)BuildSet(_dataSources);
            }

            if (typeof(TEntity) == typeof(ProjectDataSource))
            {
                return (DbSet<TEntity>)(object)BuildSet(_projectDataSources);
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

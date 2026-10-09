using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Documentation;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.HostDocs;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC12 (§1.7) — every early return in <c>get_context</c>, <c>search</c> and <c>get_documentation</c> (including
/// "access denied") writes exactly one audit row carrying the error. The rows are observed through the real
/// <see cref="McpAuditService"/> over a mocked context (§4.7). The <c>Could not resolve data source.</c> guard in
/// <c>get_documentation</c> is audited too but is unreachable from the public parameters (an empty
/// datasource_name and table_name return the project-level export earlier), so it has no test.
/// </summary>
[TestFixture]
public class McpToolEarlyReturnAuditTests
{
    private const int AllowedProjectId = 5;
    private const int ForbiddenProjectId = 6;

    private const string AccessDenied = "Access denied: your API key does not have access to project 6.";

    [Test]
    public async Task GetContext_ResolveFailure_IsAudited()
    {
        var (logs, factory, projectContext, audit) = Arrange();
        var tool = new GetContextTool(
            new Mock<IKnowledgeGraphService>().Object, factory, new Mock<IProjectBriefService>().Object,
            projectContext, audit, NullLogger<GetContextTool>.Instance);

        var result = await tool.ExecuteAsync(project_id: ForbiddenProjectId, cancellationToken: CancellationToken.None);

        AssertError(result, AccessDenied);
        AssertSingleErrorRow(logs, "get_context", expectedProjectId: null);
    }

    [Test]
    public async Task Search_OffsetOverCap_IsAudited()
    {
        var (logs, _, projectContext, audit) = Arrange();
        var tool = BuildSearchTool(projectContext, audit);

        var result = await tool.ExecuteAsync("customer", offset: 201, cancellationToken: CancellationToken.None);

        AssertError(result, "offset must be between 0 and 200.");
        AssertSingleErrorRow(logs, "search", expectedProjectId: null);
    }

    [Test]
    public async Task Search_MissingQuery_IsAudited()
    {
        var (logs, _, projectContext, audit) = Arrange();
        var tool = BuildSearchTool(projectContext, audit);

        var result = await tool.ExecuteAsync(string.Empty, cancellationToken: CancellationToken.None);

        AssertError(result, "Missing required parameter: query");
        AssertSingleErrorRow(logs, "search", expectedProjectId: null);
    }

    [Test]
    public async Task Search_ResolveFailure_IsAudited()
    {
        var (logs, _, projectContext, audit) = Arrange();
        var tool = BuildSearchTool(projectContext, audit);

        var result = await tool.ExecuteAsync("customer", project_id: ForbiddenProjectId, cancellationToken: CancellationToken.None);

        AssertError(result, AccessDenied);
        AssertSingleErrorRow(logs, "search", expectedProjectId: null);
    }

    [Test]
    public async Task GetDocumentation_ResolveFailure_IsAudited()
    {
        var (logs, factory, projectContext, audit) = Arrange();
        var tool = BuildDocumentationTool(factory, projectContext, audit);

        var result = await tool.ExecuteAsync(project_id: ForbiddenProjectId, cancellationToken: CancellationToken.None);

        AssertError(result, AccessDenied);
        AssertSingleErrorRow(logs, "get_documentation", expectedProjectId: null);
    }

    [Test]
    public async Task GetDocumentation_UnknownDataSourceName_IsAudited()
    {
        var (logs, factory, projectContext, audit) = Arrange();
        var tool = BuildDocumentationTool(factory, projectContext, audit);

        var result = await tool.ExecuteAsync(datasource_name: "missing-source", cancellationToken: CancellationToken.None);

        AssertError(result, "Data source 'missing-source' not found in this project.");
        AssertSingleErrorRow(logs, "get_documentation", expectedProjectId: AllowedProjectId);
    }

    [Test]
    public async Task GetDocumentation_TableNotFound_IsAudited()
    {
        var (logs, factory, projectContext, audit) = Arrange();
        var tool = BuildDocumentationTool(factory, projectContext, audit);

        var result = await tool.ExecuteAsync(table_name: "missing_table", cancellationToken: CancellationToken.None);

        AssertError(result, "Could not find table 'missing_table' in any data source of this project.");
        AssertSingleErrorRow(logs, "get_documentation", expectedProjectId: AllowedProjectId);
    }

    private static void AssertError(CallToolResult result, string expectedMessage)
    {
        result.IsError.Should().BeTrue();
        result.Content.Should().ContainSingle()
            .Which.Should().BeOfType<TextContentBlock>()
            .Which.Text.Should().Be(expectedMessage);
    }

    // A resolve failure has no authorized project to attribute the row to; a failure after resolution carries the
    // project the caller was allowed into.
    private static void AssertSingleErrorRow(List<McpAuditLog> logs, string tool, int? expectedProjectId)
    {
        logs.Should().ContainSingle();
        logs[0].Tool.Should().Be(tool);
        logs[0].ErrorMessage.Should().NotBeNull();
        logs[0].ProjectId.Should().Be(expectedProjectId);
    }

    private static ProjectSearchTool BuildSearchTool(McpProjectContext projectContext, McpAuditService audit)
    {
        return new ProjectSearchTool(
            new Mock<IKnowledgeGraphService>().Object, projectContext, audit, NullLogger<ProjectSearchTool>.Instance);
    }

    private static ProjectGetDocumentationTool BuildDocumentationTool(
        IDbContextFactory<BeaconContext> factory, McpProjectContext projectContext, McpAuditService audit)
    {
        return new ProjectGetDocumentationTool(
            new Mock<IKnowledgeGraphService>().Object, new Mock<IProjectDocumentationService>().Object,
            factory, projectContext, audit, NullLogger<ProjectGetDocumentationTool>.Instance);
    }

    private static (List<McpAuditLog> Logs, IDbContextFactory<BeaconContext> Factory, McpProjectContext ProjectContext, McpAuditService Audit) Arrange()
    {
        var logs = new List<McpAuditLog>();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new CapturingContext(logs));

        var audit = new McpAuditService(
            factory.Object, SettingsProviderMock.Create().Object, new HttpContextAccessor(),
            Options.Create(new McpDeploymentOptions()), NullLogger<McpAuditService>.Instance, new McpAuditOutcome(),
            Options.Create(new BeaconTelemetryOptions()), NullLoggerFactory.Instance);

        var projectContext = new McpProjectContext { UserId = 1, ApiKeyId = 9, AllowedProjectIds = [AllowedProjectId] };

        return (logs, factory.Object, projectContext, audit);
    }

    private sealed class CapturingContext : BeaconContext
    {
        private static readonly DbContextOptions<CapturingContext> Options =
            new DbContextOptionsBuilder<CapturingContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly Mock<DbSet<McpAuditLog>> _auditSet = new();

        public CapturingContext(List<McpAuditLog> logs) : base(Options, "beacon")
        {
            _auditSet.Setup(x => x.Add(It.IsAny<McpAuditLog>()))
                .Callback<McpAuditLog>(x => logs.Add(x));
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpAuditLog))
            {
                return (DbSet<TEntity>)(object)_auditSet.Object;
            }

            if (typeof(TEntity) == typeof(ProjectDataSource) || typeof(TEntity) == typeof(DatabaseMetadata))
            {
                return BuildEmptySet<TEntity>();
            }

            return base.Set<TEntity>();
        }

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        private static DbSet<T> BuildEmptySet<T>() where T : class
        {
            var data = new List<T>().AsQueryable();
            var set = new Mock<DbSet<T>>();
            set.As<IAsyncEnumerable<T>>()
                .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                .Returns(() => new TestAsyncEnumerator<T>(data.GetEnumerator()));
            set.As<IQueryable<T>>().Setup(x => x.Provider).Returns(new TestAsyncQueryProvider<T>(data.Provider));
            set.As<IQueryable<T>>().Setup(x => x.Expression).Returns(data.Expression);
            set.As<IQueryable<T>>().Setup(x => x.ElementType).Returns(data.ElementType);
            set.As<IQueryable<T>>().Setup(x => x.GetEnumerator()).Returns(() => data.GetEnumerator());

            return set.Object;
        }
    }
}

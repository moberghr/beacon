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
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// The <c>dry_run</c> MCP tool: validates SQL through every safety gate (guardrail, AST read-only,
/// schema catalog, provider dry-run) WITHOUT executing. Gate issues are collected — not
/// first-failure-wins — except the provider dry-run, which only runs when every prior gate passed
/// (EXPLAIN on a known-write statement would be unsafe). The AST and schema validators are the real
/// implementations (both are concrete classes with no DB dependency); data-source resolution runs
/// against async-queryable doubles (§4.7), mirroring FeedbackToolTests.
/// </summary>
[TestFixture]
public class DryRunToolTests
{
    private const int ProjectId = 42;
    private const int DataSourceId = 7;
    private const int ApiDataSourceId = 8;
    private const string ValidSql = "SELECT id, name FROM customers";

    private Mock<IQueryGuardrailService> _guardrail = null!;
    private Mock<IQueryExecutionService> _queryExecution = null!;
    private Mock<IKnowledgeGraphService> _knowledgeGraph = null!;
    private List<McpAuditLog> _auditLogs = null!;

    [SetUp]
    public void SetUp()
    {
        _guardrail = new Mock<IQueryGuardrailService>();
        _queryExecution = new Mock<IQueryExecutionService>();
        _knowledgeGraph = new Mock<IKnowledgeGraphService>();
        _auditLogs = [];

        _knowledgeGraph
            .Setup(x => x.GetSchemaCatalogAsync(DataSourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["customers"] = ["id", "name", "email"],
                ["public.customers"] = ["id", "name", "email"]
            });

        _guardrail
            .Setup(x => x.ApplyRowLimit(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>()))
            .Returns<string, int, string?>((sql, maxRows, _) => $"{sql} LIMIT {maxRows}");

        SetupGuardrailValidation(isValid: true, error: null, piiColumns: null);

        _queryExecution
            .Setup(x => x.ValidateAsync(DataSourceId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
    }

    [Test]
    public async Task ValidSelect_AllGatesPass_AppliesRowLimit_AndRunsProviderDryRun()
    {
        var result = await CreateTool().ExecuteAsync(
            datasource_id: DataSourceId, sql: ValidSql, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        var text = GetText(result);
        text.Should().Contain("# Dry Run");
        text.Should().Contain("VALID");
        text.Should().NotContain("INVALID");
        text.Should().Contain("- ✓ guardrail");
        text.Should().Contain("- ✓ ast");
        text.Should().Contain("- ✓ schema");
        text.Should().Contain("- ✓ provider_dry_run");
        text.Should().Contain("### SQL that would execute");
        text.Should().Contain($"{ValidSql} LIMIT 100");

        _queryExecution.Verify(
            x => x.ValidateAsync(DataSourceId, ValidSql, It.IsAny<CancellationToken>()), Times.Once);

        var structured = result.StructuredContent!.Value;
        structured.GetProperty("valid").GetBoolean().Should().BeTrue();
        structured.GetProperty("executable_sql").GetString().Should().Be($"{ValidSql} LIMIT 100");
        structured.GetProperty("issues").EnumerateArray().Should().BeEmpty();

        _auditLogs.Should().ContainSingle("§1.7 — the success path must record an audit row");
        _auditLogs[0].Tool.Should().Be("dry_run");
        _auditLogs[0].ErrorMessage.Should().BeNull();
        _auditLogs[0].DataSourceId.Should().Be(DataSourceId);
        _auditLogs[0].ProjectId.Should().Be(ProjectId);
    }

    [Test]
    public async Task Insert_CollectsGuardrailAndAstIssues_AndSkipsProviderDryRun()
    {
        const string insertSql = "INSERT INTO customers (id) VALUES (1)";
        SetupGuardrailValidation(isValid: false, error: "Only SELECT queries are allowed", piiColumns: null);

        var result = await CreateTool().ExecuteAsync(
            datasource_id: DataSourceId, sql: insertSql, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse("an INVALID verdict is still a successful dry run");
        var text = GetText(result);
        text.Should().Contain("INVALID");
        text.Should().Contain("- ✗ guardrail — Only SELECT queries are allowed");
        text.Should().Contain("- ✗ ast");
        text.Should().Contain("- – provider_dry_run — skipped");
        text.Should().NotContain("### SQL that would execute");

        _queryExecution.Verify(
            x => x.ValidateAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "EXPLAIN on a known-write statement must never run");

        var structured = result.StructuredContent!.Value;
        structured.GetProperty("valid").GetBoolean().Should().BeFalse();
        structured.GetProperty("executable_sql").ValueKind.Should().Be(JsonValueKind.Null);
        var gates = structured.GetProperty("issues").EnumerateArray()
            .Select(x => x.GetProperty("gate").GetString())
            .ToList();
        gates.Should().Contain("guardrail");
        gates.Should().Contain("ast");
    }

    [Test]
    public async Task HallucinatedColumn_ReportsSchemaGateIssue()
    {
        const string sql = "SELECT nonexistent_col FROM customers";

        var result = await CreateTool().ExecuteAsync(
            datasource_id: DataSourceId, sql: sql, cancellationToken: CancellationToken.None);

        var text = GetText(result);
        text.Should().Contain("INVALID");
        text.Should().Contain("- ✗ schema");
        text.Should().Contain("nonexistent_col");

        var structured = result.StructuredContent!.Value;
        structured.GetProperty("valid").GetBoolean().Should().BeFalse();
        var issues = structured.GetProperty("issues").EnumerateArray().ToList();
        issues.Should().ContainSingle(x => x.GetProperty("gate").GetString() == "schema");
        _queryExecution.Verify(
            x => x.ValidateAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task EmptyCatalog_AddsSchemaSkipAdvisory_InvalidatesVerdict_ButProviderDryRunStillRuns()
    {
        _knowledgeGraph
            .Setup(x => x.GetSchemaCatalogAsync(DataSourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase));

        var result = await CreateTool().ExecuteAsync(
            datasource_id: DataSourceId, sql: ValidSql, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse("an INVALID verdict is still a successful dry run");
        var text = GetText(result);
        text.Should().Contain("INVALID", "a validation the tool could not perform must not read as valid");
        text.Should().NotContain("✓ schema", "the column check was skipped, so it must never render as passed");
        text.Should().Contain("- ✗ schema — No schema metadata available for this data source yet");
        text.Should().Contain("- ✓ provider_dry_run");

        _queryExecution.Verify(
            x => x.ValidateAsync(DataSourceId, ValidSql, It.IsAny<CancellationToken>()), Times.Once,
            "the provider dry-run must still run when the ONLY issue is the schema-skip advisory — the parsers passed");

        var structured = result.StructuredContent!.Value;
        structured.GetProperty("valid").GetBoolean().Should().BeFalse();
        var issues = structured.GetProperty("issues").EnumerateArray().ToList();
        issues.Should().ContainSingle(x => x.GetProperty("gate").GetString() == "schema");
        issues[0].GetProperty("error").GetString().Should().Contain("column check was skipped");
    }

    [Test]
    public async Task ProviderDryRunError_ReturnsInvalidWithProviderIssue()
    {
        const string providerError = "relation \"customers\" does not exist";
        _queryExecution
            .Setup(x => x.ValidateAsync(DataSourceId, ValidSql, It.IsAny<CancellationToken>()))
            .ReturnsAsync(providerError);

        var result = await CreateTool().ExecuteAsync(
            datasource_id: DataSourceId, sql: ValidSql, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        var text = GetText(result);
        text.Should().Contain("INVALID");
        text.Should().Contain($"- ✗ provider_dry_run — {providerError}");

        var structured = result.StructuredContent!.Value;
        structured.GetProperty("valid").GetBoolean().Should().BeFalse();
        var issues = structured.GetProperty("issues").EnumerateArray().ToList();
        issues.Should().ContainSingle(x => x.GetProperty("gate").GetString() == "provider_dry_run");
        issues[0].GetProperty("error").GetString().Should().Be(providerError);
        structured.GetProperty("executable_sql").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Test]
    public async Task MissingSql_ReturnsError()
    {
        var result = await CreateTool().ExecuteAsync(
            datasource_id: DataSourceId, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text
            .Should().Be("Missing required parameter: sql");
        _queryExecution.Verify(
            x => x.ValidateAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        _auditLogs.Should().ContainSingle("§1.7 — early-exit errors must still record an audit row");
        _auditLogs[0].Tool.Should().Be("dry_run");
        _auditLogs[0].ErrorMessage.Should().Be("Missing required parameter: sql");
    }

    [Test]
    public async Task ApiDataSource_IsRejected_SqlOnly_AndAudited()
    {
        var result = await CreateTool().ExecuteAsync(
            datasource_id: ApiDataSourceId, sql: ValidSql, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeTrue();
        GetText(result).Should().Be("dry_run validates SQL only — API data sources are not supported.");
        _queryExecution.Verify(
            x => x.ValidateAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "no gate may run against an API source");

        _auditLogs.Should().ContainSingle("§1.7 — the API-source rejection must record an audit row");
        _auditLogs[0].Tool.Should().Be("dry_run");
        _auditLogs[0].ErrorMessage.Should().Be("dry_run validates SQL only — API data sources are not supported.");
    }

    [Test]
    public async Task SchemaCatalogThrows_CatchPathAudits_WithResolvedProjectId()
    {
        _knowledgeGraph
            .Setup(x => x.GetSchemaCatalogAsync(DataSourceId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("catalog offline"));

        var result = await CreateTool().ExecuteAsync(
            datasource_id: DataSourceId, sql: ValidSql, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeTrue();
        _auditLogs.Should().ContainSingle("§1.7 — the catch path must record an audit row");
        _auditLogs[0].Tool.Should().Be("dry_run");
        _auditLogs[0].ErrorMessage.Should().Be("catalog offline");
        _auditLogs[0].ProjectId.Should().Be(ProjectId,
            "the catch path passes the resolved projectId raw, matching FailAsync");
    }

    [Test]
    public async Task StructuredPayload_CarriesValidIssuesExecutableSqlAndPiiColumns()
    {
        SetupGuardrailValidation(isValid: true, error: null, piiColumns: ["email"]);

        var result = await CreateTool().ExecuteAsync(
            datasource_id: DataSourceId, sql: ValidSql, cancellationToken: CancellationToken.None);

        GetText(result).Should().Contain("**PII columns that would be masked:** email");

        var structured = result.StructuredContent!.Value;
        structured.GetProperty("valid").GetBoolean().Should().BeTrue();
        structured.GetProperty("issues").EnumerateArray().Should().BeEmpty();
        structured.GetProperty("executable_sql").GetString().Should().Be($"{ValidSql} LIMIT 100");
        structured.GetProperty("pii_columns").EnumerateArray()
            .Select(x => x.GetString())
            .Should().Equal("email");
    }

    private void SetupGuardrailValidation(bool isValid, string? error, List<string>? piiColumns)
    {
        _guardrail
            .Setup(x => x.ValidateQuery(It.IsAny<string>(), It.IsAny<QueryGuardrailOptions?>()))
            .Returns(new QueryValidationResult(isValid, error, isValid, piiColumns));
    }

    private DryRunTool CreateTool()
    {
        // One factory serves both the tool (data-source resolution) and the audit service; each
        // CreateDbContextAsync call gets a fresh context over the same captured audit-log list
        // (§4.7 — async-queryable doubles, no DB). Mirrors FeedbackToolTests.
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new DryRunTestContext(_auditLogs));

        var settingsProvider = new Mock<IMcpSettingsProvider>();
        settingsProvider.Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpSettingsData());

        var projectContext = new McpProjectContext { UserId = 1, AllowedProjectIds = [ProjectId] };

        var auditService = new McpAuditService(factory.Object, NullLogger<McpAuditService>.Instance);

        return new DryRunTool(
            factory.Object,
            _guardrail.Object,
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            new SqlSchemaValidator(),
            _knowledgeGraph.Object,
            _queryExecution.Object,
            settingsProvider.Object,
            projectContext,
            auditService,
            NullLogger<DryRunTool>.Instance);
    }

    private static string GetText(CallToolResult result)
    {
        return result.Content.OfType<TextContentBlock>().Single().Text;
    }

    /// <summary>Serves the data-source resolution queries (ProjectDataSources membership + DataSources
    /// lookup) over async-queryable doubles — no DB, no UseInMemoryDatabase (§4.7) — and captures the
    /// McpAuditLog rows the real McpAuditService writes, mirroring FeedbackToolTests.</summary>
    private sealed class DryRunTestContext : BeaconContext
    {
        private static readonly DbContextOptions<DryRunTestContext> Options =
            new DbContextOptionsBuilder<DryRunTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly Mock<DbSet<McpAuditLog>> _auditSet = new();

        public DryRunTestContext(List<McpAuditLog> auditLogs) : base(Options, "beacon")
        {
            _auditSet.Setup(x => x.Add(It.IsAny<McpAuditLog>()))
                .Callback<McpAuditLog>(auditLogs.Add);
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpAuditLog))
            {
                return (DbSet<TEntity>)(object)_auditSet.Object;
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
                    },
                    new()
                    {
                        Id = ApiDataSourceId,
                        Name = "crm-api",
                        DataSourceType = DataSourceType.Api,
                        EncryptedConnectionData = "encrypted"
                    }
                });
            }

            if (typeof(TEntity) == typeof(ProjectDataSource))
            {
                return (DbSet<TEntity>)(object)BuildSet(new List<ProjectDataSource>
                {
                    new() { ProjectId = ProjectId, DataSourceId = DataSourceId },
                    new() { ProjectId = ProjectId, DataSourceId = ApiDataSourceId }
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

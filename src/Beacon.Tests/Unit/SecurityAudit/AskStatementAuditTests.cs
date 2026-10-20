using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Mcp;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Retention;
using Beacon.Core.Services.Security;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// The evidence an <c>ask</c> audit row keeps: one child row per statement that reached a data source (its purpose, data
/// source, keyed hash, tables, rows, time and error class; its text only while the project retains query content), the
/// data source, row count and tables of a single-source answer, and a keyed hash of what the call received — written on
/// the success and the failure path alike, whatever the learning settings.
/// </summary>
[TestFixture]
public class AskStatementAuditTests
{
    private const int ProjectId = 42;
    private const int DataSourceId = 7;
    private const string Question = "How many orders did ada@corp.example place?";
    private const string Sql = "SELECT count(*) FROM public.orders WHERE email = 'ada@corp.example'";

    [Test]
    public async Task EachStatement_IsAChildRow_WithItsTextWhileContentIsRetained()
    {
        var log = new McpStatementLog();
        log.Record(new McpStatement(DataSourceId, McpStatementPurposes.Initial, Sql, ["public.orders"], 0, 14, McpContentRedactor.ClassSchema));
        log.Record(new McpStatement(DataSourceId, McpStatementPurposes.Repair, "SELECT 1 FROM public.orders", ["public.orders"], 1, 9, null));

        var row = await AuditAsync(log, retainContent: true);

        row.Statements.Select(x => (x.Purpose, x.DataSourceId, x.RowCount, x.DurationMs, x.ErrorClass)).Should().Equal(
            (McpStatementPurposes.Initial, (int?)DataSourceId, (int?)0, (int?)14, McpContentRedactor.ClassSchema),
            (McpStatementPurposes.Repair, (int?)DataSourceId, (int?)1, (int?)9, (string?)null));
        row.Statements[0].Sql.Should().Be(Sql);
        row.Statements[0].SqlHash.Should().Be(TestMcpAudit.ContentHasher.Hash(Sql));
        row.Statements[0].TablesJson.Should().Be("[\"public.orders\"]");
        row.Statements.Should().OnlyContain(x => x.ProjectId == ProjectId);
    }

    [Test]
    public async Task WithoutContentRetention_AStatementKeepsItsHashAndStructure_NeverItsText()
    {
        var log = new McpStatementLog();
        log.Record(new McpStatement(DataSourceId, McpStatementPurposes.Initial, Sql, ["public.orders"], 3, 14, null));

        var row = await AuditAsync(log, retainContent: false);

        var statement = row.Statements.Should().ContainSingle().Subject;
        statement.Sql.Should().BeNull();
        statement.SqlHash.Should().Be(TestMcpAudit.ContentHasher.Hash(Sql));
        statement.TablesJson.Should().Be("[\"public.orders\"]");
        row.Parameters.Should().NotContain("ada@corp.example");
        JsonDocument.Parse(row.Parameters!).RootElement.GetProperty("tables").EnumerateArray().Select(x => x.GetString())
            .Should().Equal(["public.orders"], "an ask row without its own table list names the tables its statements read");
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task TheContentHash_IsWritten_WhetherOrNotContentIsRetained(bool retainContent)
    {
        var row = await AuditAsync(new McpStatementLog(), retainContent);
        var other = await AuditAsync(new McpStatementLog(), retainContent, parameters: "How many orders last week?");

        row.ContentHash.Should().Be(TestMcpAudit.ContentHasher.Hash(Question)).And.MatchRegex("^[0-9a-f]{64}$");
        other.ContentHash.Should().NotBe(row.ContentHash);
        row.ContentHash.Should().NotBe(Sha256Of(Question), "the hash is keyed: nobody without the key can test a guess against it");
    }

    [Test]
    public async Task TheFailurePath_StillWritesTheStatements_WithLearningOff()
    {
        var log = new McpStatementLog();
        log.Record(new McpStatement(DataSourceId, McpStatementPurposes.Initial, Sql, ["public.orders"], null, 30000, McpContentRedactor.ClassTimeout));

        var row = await AuditAsync(log, retainContent: true, errorMessage: "canceling statement due to statement timeout", learning: false);

        row.ErrorMessage.Should().NotBeNull();
        row.Statements.Should().ContainSingle().Which.ErrorClass.Should().Be(McpContentRedactor.ClassTimeout);
    }

    [Test]
    public async Task EachStatement_IsTakenOnce()
    {
        var log = new McpStatementLog();
        log.Record(new McpStatement(DataSourceId, McpStatementPurposes.Query, Sql, [], 1, 1, null));
        var logs = new List<McpAuditLog>();
        var service = AuditService(logs, log, retainContent: true);

        await service.LogToolCallAsync(1, "query", Sql, DataSourceId, ProjectId, 5, 1, null, ct: CancellationToken.None);
        await service.LogToolCallAsync(1, "query", Sql, DataSourceId, ProjectId, 5, 1, null, ct: CancellationToken.None);

        logs.Select(x => x.Statements.Count).Should().Equal(1, 0);
    }

    [Test]
    public async Task TheExecutionService_RecordsTheStatementAsItRan_UnderThePurposeItRunsFor()
    {
        var log = new McpStatementLog();
        var provider = Provider(new ProviderQueryResult { Success = true, Rows = [Row(1), Row(2)] });
        var service = QueryExecution(log, provider);

        await service.ExecuteAsync(DataSourceId, "SELECT id FROM public.orders", 50, CancellationToken.None);
        using (log.BeginPurpose(McpStatementPurposes.Repair))
        {
            await service.ExecuteAsync(DataSourceId, "SELECT id FROM public.orders", 50, CancellationToken.None);
        }

        await service.ValidateAsync(DataSourceId, "SELECT id FROM public.orders", CancellationToken.None);

        var statements = log.TakeAll();
        statements.Select(x => x.Purpose).Should().Equal(McpStatementPurposes.Query, McpStatementPurposes.Repair, McpStatementPurposes.DryRun);
        statements[0].Should().BeEquivalentTo(new { DataSourceId = (int?)DataSourceId, Sql = "SELECT id FROM public.orders LIMIT 50", RowCount = (int?)2, ErrorClass = (string?)null });
        statements[0].Tables.Should().Equal("public.orders");
        statements[2].RowCount.Should().BeNull();
    }

    [Test]
    public async Task AStatementThatFails_IsRecordedWithItsClass_AndOneThatThrows_IsRecordedAndRethrown()
    {
        var log = new McpStatementLog();
        var failing = QueryExecution(log, Provider(new ProviderQueryResult { Success = false, ErrorMessage = "permission denied for table orders" }));
        await failing.ExecuteAsync(DataSourceId, "SELECT id FROM orders", 50, CancellationToken.None);
        var throwing = new Mock<IDataSourceProvider>();
        throwing
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Timeout expired while reading orders of ada@corp.example"));

        var act = () => QueryExecution(log, throwing).ExecuteAsync(DataSourceId, "SELECT id FROM orders", 50, CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
        log.TakeAll().Select(x => x.ErrorClass).Should().Equal(McpContentRedactor.ClassPermission, McpContentRedactor.ClassTimeout);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ASingleSourceAsk_RecordsItsSourceRowsTablesAndStatements_OnTheAuditRow(bool retainContent)
    {
        var log = new McpStatementLog();
        var logs = new List<McpAuditLog>();
        var execution = QueryExecution(log, Provider(new ProviderQueryResult { Success = true, Rows = [Row(1), Row(2), Row(3)] }));

        var result = await AskAsync(log, logs, execution, retainContent, failAfterExecution: false);

        (result.IsError ?? false).Should().BeFalse();
        var row = logs.Should().ContainSingle().Subject;
        row.Should().BeEquivalentTo(new { Tool = "ask", DataSourceId = (int?)DataSourceId, ResultRowCount = (int?)3, ProjectId = (int?)ProjectId }, x => x.ExcludingMissingMembers());
        row.ContentHash.Should().Be(TestMcpAudit.ContentHasher.Hash(Question));
        var statement = row.Statements.Should().ContainSingle().Subject;
        statement.Should().BeEquivalentTo(new { Purpose = McpStatementPurposes.Initial, DataSourceId = (int?)DataSourceId, RowCount = (int?)3 }, x => x.ExcludingMissingMembers());
        statement.SqlHash.Should().Be(TestMcpAudit.ContentHasher.Hash("SELECT id FROM public.orders LIMIT 100"));
        statement.Sql.Should().Be(retainContent ? "SELECT id FROM public.orders LIMIT 100" : null);
        if (!retainContent)
        {
            JsonDocument.Parse(row.Parameters!).RootElement.GetProperty("tables").EnumerateArray().Select(x => x.GetString())
                .Should().Equal(["orders"], "the answer's own tables");
        }
    }

    [Test]
    public async Task AnAskThatFailsAfterAStatementRan_StillWritesTheStatement()
    {
        var log = new McpStatementLog();
        var logs = new List<McpAuditLog>();
        var execution = QueryExecution(log, Provider(new ProviderQueryResult { Success = true, Rows = [Row(1)] }));

        var result = await AskAsync(log, logs, execution, retainContent: true, failAfterExecution: true);

        (result.IsError ?? false).Should().BeTrue();
        var row = logs.Should().ContainSingle().Subject;
        row.ErrorMessage.Should().NotBeNull();
        row.Statements.Should().ContainSingle().Which.Purpose.Should().Be(McpStatementPurposes.Initial);
    }

    // MaxConcurrentQueriesPerKey: a call over the caller's limit is refused before any work, and the refusal is audited
    // like every other tool error (§1.7/§9.5).
    [Test]
    public async Task AnAskOverTheConcurrencyLimit_IsRefusedBeforeAnyStatement_AndAudited()
    {
        var log = new McpStatementLog();
        var logs = new List<McpAuditLog>();
        var limiter = new McpQueryConcurrencyLimiter();
        using var running = limiter.TryAcquire("user:1", 1);
        var execution = QueryExecution(log, Provider(new ProviderQueryResult { Success = true, Rows = [Row(1)] }));

        var result = await AskAsync(log, logs, execution, retainContent: true, failAfterExecution: false, limiter, maxConcurrentQueries: 1);

        (result.IsError ?? false).Should().BeTrue();
        result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text.Should().Be(McpQueryConcurrencyLimiter.RefusalMessage);
        var row = logs.Should().ContainSingle().Subject;
        row.Should().BeEquivalentTo(new { Tool = "ask", ErrorMessage = McpQueryConcurrencyLimiter.RefusalMessage, ProjectId = (int?)ProjectId }, x => x.ExcludingMissingMembers());
        row.Statements.Should().BeEmpty("nothing reached a data source");
        limiter.InFlight("user:1").Should().Be(1, "the refused call took no slot");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AnAsk_ReleasesItsSlot_WhetherItSucceedsOrFails(bool failAfterExecution)
    {
        var log = new McpStatementLog();
        var logs = new List<McpAuditLog>();
        var limiter = new McpQueryConcurrencyLimiter();
        var execution = QueryExecution(log, Provider(new ProviderQueryResult { Success = true, Rows = [Row(1)] }));

        var result = await AskAsync(log, logs, execution, retainContent: true, failAfterExecution, limiter, maxConcurrentQueries: 1);

        (result.IsError ?? false).Should().Be(failAfterExecution);
        limiter.InFlight("user:1").Should().Be(0);
        limiter.TryAcquire("user:1", 1).Should().NotBeNull("the slot is free for the next call");
    }

    [Test]
    public async Task ACallThatRanMoreStatementsThanTheLogKeeps_EndsWithAMarkerRow_CountingTheRest_AndAWarning()
    {
        var log = new McpStatementLog();
        for (var i = 0; i < McpStatementLog.MaxStatements + 7; i++)
        {
            log.Record(new McpStatement(DataSourceId, McpStatementPurposes.Candidate, "SELECT 1", [], 1, 1, null));
        }

        var logs = new List<McpAuditLog>();
        var recorder = new LogRecorder();
        await AuditService(logs, log, retainContent: false, logger: recorder.For<McpAuditService>())
            .LogToolCallAsync(1, "ask", Question, null, ProjectId, 20, null, null, ct: CancellationToken.None);

        var statements = logs.Should().ContainSingle().Subject.Statements;
        statements.Should().HaveCount(McpStatementLog.MaxStatements + 1);
        statements[^1].Should().BeEquivalentTo(
            new { Purpose = McpStatementPurposes.Truncated, RowCount = (int?)7, Sql = (string?)null, SqlHash = (string?)null },
            x => x.ExcludingMissingMembers());
        recorder.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning).Which.Message.Should().Contain("7").And.NotContain("SELECT");
    }

    [Test]
    public async Task ATableListThatDoesNotFit_EndsWithHowManyMoreThereAre_AndOneThatCouldNotBeRead_SaysSo()
    {
        var log = new McpStatementLog();
        var tables = Enumerable.Range(0, 400).Select(x => $"schema_{x:D3}.table_{x:D3}").ToList();
        log.Record(new McpStatement(DataSourceId, McpStatementPurposes.Initial, "SELECT 1", tables, 1, 1, null));
        var unreadable = new McpStatementLog();

        // A statement whose text cannot be scanned (here: none at all) still goes on record, its tables marked unknown.
        await unreadable.RecordAsync(DataSourceId, null, null!, () => Task.FromResult(1), _ => (1, null));
        var row = await AuditAsync(log, retainContent: false);

        var listed = JsonSerializer.Deserialize<List<string>>(row.Statements[0].TablesJson!)!;
        row.Statements[0].TablesJson!.Length.Should().BeLessThanOrEqualTo(4000);
        listed[^1].Should().Be(McpStatementTables.More(tables.Count - (listed.Count - 1)));
        listed.Take(listed.Count - 1).Should().Equal(tables.Take(listed.Count - 1));
        unreadable.TakeAll().Should().ContainSingle().Which.Tables.Should().Equal([McpStatementTables.Unparsed], "a statement whose tables cannot be read never claims it read none");
    }

    [Test]
    public async Task AnAbsentInput_HashesApartFromAnEmptyOne()
    {
        var absent = await AuditAsync(new McpStatementLog(), retainContent: false, parameters: null);
        var empty = await AuditAsync(new McpStatementLog(), retainContent: false, parameters: string.Empty);

        absent.ContentHash.Should().NotBe(empty.ContentHash);
    }

    [Test]
    public async Task AnEmptyErrorMessage_IsNoError()
    {
        var row = await AuditAsync(new McpStatementLog(), retainContent: true, errorMessage: string.Empty);

        row.ErrorMessage.Should().BeNull();
    }

    [Test]
    public async Task WhenTheDeploymentRequiresContent_AnUnreadableSetting_KeepsTheContent_AndIsAnError()
    {
        var logs = new List<McpAuditLog>();
        var recorder = new LogRecorder();
        var settings = new Mock<IMcpSettingsProvider>();
        settings
            .Setup(x => x.GetEffectiveSettingsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the settings store is down"));
        var log = new McpStatementLog();
        log.Record(new McpStatement(DataSourceId, McpStatementPurposes.Initial, Sql, ["public.orders"], 1, 1, null));

        await AuditService(
                logs,
                log,
                retainContent: false,
                logger: recorder.For<McpAuditService>(),
                settings: settings.Object,
                deployment: new McpDeploymentOptions { Audit = new McpAuditOptions { RequireContent = true } })
            .LogToolCallAsync(1, "ask", Question, null, ProjectId, 20, null, null, ct: CancellationToken.None);

        var row = logs.Should().ContainSingle().Subject;
        row.Parameters.Should().Be(Question, "the deployment pins content retention on");
        row.Statements.Single().Sql.Should().Be(Sql);
        recorder.Entries.Should().ContainSingle(x => x.Level == LogLevel.Error)
            .Which.Message.Should().NotContain("the settings store is down").And.NotContain("ada@corp.example");
    }

    private static Dictionary<string, object?> Row(int id) => new() { ["id"] = id };

    private static string Sha256Of(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    private static async Task<McpAuditLog> AuditAsync(
        IMcpStatementLog log,
        bool retainContent,
        string? parameters = Question,
        string? errorMessage = null,
        bool learning = true)
    {
        var logs = new List<McpAuditLog>();
        var service = AuditService(logs, log, retainContent, learning);

        await service.LogToolCallAsync(1, "ask", parameters, null, ProjectId, 20, null, errorMessage, ct: CancellationToken.None);

        return logs.Should().ContainSingle().Subject;
    }

    private static McpAuditService AuditService(
        List<McpAuditLog> logs,
        IMcpStatementLog log,
        bool retainContent,
        bool learning = true,
        ILogger<McpAuditService>? logger = null,
        IMcpSettingsProvider? settings = null,
        McpDeploymentOptions? deployment = null)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AuditCapturingContext(logs));

        return new McpAuditService(
            factory.Object,
            settings ?? SettingsProviderMock.Create(new McpSettingsData { RetainQueryContent = retainContent, EnableLearning = learning }).Object,
            new HttpContextAccessor(),
            Options.Create(deployment ?? new McpDeploymentOptions()),
            logger ?? NullLogger<McpAuditService>.Instance,
            new McpAuditOutcome(),
            Options.Create(new BeaconTelemetryOptions()),
            NullLoggerFactory.Instance,
            TestMcpAudit.ContentHasher,
            log);
    }

    private static Mock<IDataSourceProvider> Provider(ProviderQueryResult result)
    {
        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        provider
            .Setup(x => x.ValidateQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Beacon.Core.Models.Providers.QueryValidationResult { IsValid = true });

        return provider;
    }

    private static QueryExecutionService QueryExecution(IMcpStatementLog log, Mock<IDataSourceProvider> provider)
    {
        var sources = new List<DataSource>
        {
            new() { Id = DataSourceId, Name = "warehouse", DataSourceType = DataSourceType.Database, DatabaseEngineType = DatabaseEngineType.PostgreSQL, EncryptedConnectionData = "encrypted" }
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(new Dictionary<Type, object> { [typeof(DataSource)] = RecordingBeaconContext.MemorySet(sources, []) }, []));
        var providers = new Mock<IDataSourceProviderFactory>();
        providers
            .Setup(x => x.GetProvider(It.IsAny<DataSourceType>()))
            .Returns(provider.Object);
        var settings = SettingsProviderMock.Create(new McpSettingsData { EnablePiiDetection = false });

        return new QueryExecutionService(
            factory.Object,
            providers.Object,
            new QueryGuardrailService(),
            SettingsProviderMock.Unshared(settings.Object),
            new McpProjectContext { UserId = 1, ActiveProjectId = ProjectId, AllowedProjectIds = [ProjectId] },
            log,
            TestQueryCostLimit.Passing,
            new Beacon.Core.HostData.HostDataSourceGuard(new TestHostDataSources()),
            TestSqlGate.Create(),
            TestConfiguredDatabases.None);
    }

    // The ask tool over a routed single source, its pipeline running the generated SQL once through the real execution
    // service (as the pipeline's executor does), then failing or answering.
    private static async Task<ModelContextProtocol.Protocol.CallToolResult> AskAsync(
        IMcpStatementLog log,
        List<McpAuditLog> logs,
        QueryExecutionService execution,
        bool retainContent,
        bool failAfterExecution,
        McpQueryConcurrencyLimiter? limiter = null,
        int maxConcurrentQueries = 4)
    {
        var settingsProvider = SettingsProviderMock.Create(new McpSettingsData { RetainQueryContent = retainContent, MaxConcurrentQueriesPerKey = maxConcurrentQueries });
        var llm = new Mock<ILlmProvider>();
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(x => x.GetService(typeof(ILlmProvider))).Returns(llm.Object);
        var intent = new Mock<IIntentClassifier>();
        intent
            .Setup(x => x.ClassifyAsync(It.IsAny<ILlmProvider>(), Question, It.IsAny<CancellationToken>()))
            .ReturnsAsync(IntentClassification.DataQuery);
        var knowledgeGraph = new Mock<IKnowledgeGraphService>();
        knowledgeGraph
            .Setup(x => x.GetProjectDataSourcesAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DataSourceKnowledge { DataSourceId = DataSourceId, Name = "warehouse" }]);
        var router = new Mock<IDataSourceRouter>();
        router
            .Setup(x => x.RouteAsync(It.IsAny<ILlmProvider>(), It.IsAny<List<DataSourceKnowledge>>(), Question, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoutingResult { Sources = [new RoutedSource { DataSourceId = DataSourceId, DataSourceName = "warehouse", Reason = "orders" }] });
        var pipeline = new Mock<IAskSqlPipeline>();
        pipeline
            .Setup(x => x.RunAsync(It.IsAny<ILlmProvider>(), DataSourceId, ProjectId, Question, It.IsAny<McpSettingsData>(), It.IsAny<IAskSqlExecutor>(), It.IsAny<AskSqlPipelineOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async (ILlmProvider _, int dataSourceId, int _, string _, McpSettingsData _, IAskSqlExecutor executor, AskSqlPipelineOptions _, CancellationToken ct) =>
            {
                AskExecutionResult result;
                using (log.BeginPurpose(McpStatementPurposes.Initial))
                {
                    result = await executor.ExecuteAsync(dataSourceId, "SELECT id FROM public.orders", 100, ct);
                }

                if (failAfterExecution)
                {
                    throw new InvalidOperationException("the provider went away");
                }

                return new AskSqlOutcome(
                    "SELECT id FROM public.orders", "SELECT id FROM public.orders", ["orders"], result.IsSuccess, result.FormattedResult ?? "", result,
                    null, null, null, [], false, null, [], null, [], null, []);
            });
        var signalFactory = new Mock<IDbContextFactory<BeaconContext>>();
        var auditFactory = new Mock<IDbContextFactory<BeaconContext>>();
        auditFactory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AuditCapturingContext(logs));
        var tool = new ProjectAskTool(
            knowledgeGraph.Object,
            settingsProvider.Object,
            SettingsProviderMock.Unshared(settingsProvider.Object),
            serviceProvider.Object,
            new McpProjectContext { UserId = 1, AllowedProjectIds = [ProjectId], QueryProjectIds = [ProjectId] },
            new McpAuditService(
                auditFactory.Object,
                settingsProvider.Object,
                new HttpContextAccessor(),
                Options.Create(new McpDeploymentOptions()),
                NullLogger<McpAuditService>.Instance,
                new McpAuditOutcome(),
                Options.Create(new BeaconTelemetryOptions()),
                NullLoggerFactory.Instance,
                TestMcpAudit.ContentHasher,
                log),
            new McpSignalService(signalFactory.Object, settingsProvider.Object, NullLogger<McpSignalService>.Instance),
            pipeline.Object,
            new AskSqlExecutor(execution),
            intent.Object,
            router.Object,
            Mock.Of<IKnowledgeAnswerService>(),
            Mock.Of<ICrossSourceQueryService>(),
            Options.Create(new McpDeploymentOptions()),
            limiter ?? new McpQueryConcurrencyLimiter(),
            NullLogger<ProjectAskTool>.Instance);

        return await tool.ExecuteAsync(Question, cancellationToken: CancellationToken.None);
    }

    /// <summary>Captures the audit rows (with their statements) the service adds; no database (§4.7).</summary>
    private sealed class AuditCapturingContext : BeaconContext
    {
        private static readonly DbContextOptions<AuditCapturingContext> Options =
            new DbContextOptionsBuilder<AuditCapturingContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly Mock<DbSet<McpAuditLog>> _set = new();

        public AuditCapturingContext(List<McpAuditLog> logs) : base(Options, "beacon")
        {
            _set.Setup(x => x.Add(It.IsAny<McpAuditLog>())).Callback<McpAuditLog>(logs.Add);
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class =>
            typeof(TEntity) == typeof(McpAuditLog)
                ? (DbSet<TEntity>)(object)_set.Object
                : base.Set<TEntity>();

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}

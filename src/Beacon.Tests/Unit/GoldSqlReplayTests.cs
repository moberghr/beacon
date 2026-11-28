using Beacon.AI.Services.Eval;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Mcp;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.RowSecurity;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// A golden case's gold SQL is replayed once before promotion (<see cref="IMcpEvalService.ReplayGoldSqlAsync"/>): only
/// through the eval harness's read-only executor — the gate with read-only forced and the row security context from the
/// resolver, then the provider's read-only path (§1.5, §1.15) — never generation or the judge. A run gives the result's
/// fingerprint; a gate refusal, a data source error or any failure (missing data source, statement timeout) is "did not
/// run", logged by ids and failure codes only (§1.11); only the caller's cancellation propagates. A person starts the
/// replay, so a statement the gate passed is in the execution log before it is sent, as the caller by <c>Users.Id</c>, and
/// its row is closed with how it ended; while the log is required, a row that cannot be written stops the statement (§1.7).
/// The real <see cref="McpEvalService"/> over in-memory sets, no database (§4.7).
/// </summary>
[TestFixture]
public class GoldSqlReplayTests
{
    private const int DataSourceId = 10;
    private const int ProjectId = 1;
    private const string GoldSql = "SELECT id, total FROM orders";
    private const string ProviderError = "relation \"orders\" does not exist at db-secret-host";

    private static readonly List<Dictionary<string, object?>> Rows =
    [
        new() { ["id"] = 1, ["total"] = 12.5m },
        new() { ["id"] = 2, ["total"] = 7m }
    ];

    [Test]
    public async Task AGoldSqlThatRuns_GivesItsResultsFingerprint_ThroughTheGatedReadOnlyPath()
    {
        var harness = new Harness();
        harness.Succeeds(Rows);

        var replay = await harness.Service.ReplayGoldSqlAsync(DataSourceId, ProjectId, GoldSql, CancellationToken.None);

        replay.Ran.Should().BeTrue();
        replay.ResultFingerprint.Should().Be(ResultSetFingerprint.Compute(Rows));
        harness.Provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(
                It.Is<DataSource>(y => y.Id == DataSourceId),
                It.Is<string>(y => y.StartsWith(GoldSql) && y.Contains("LIMIT")),
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the gold SQL runs once, as the gate wrote it with its row cap");
        harness.Provider.VerifyNoOtherCalls();
        harness.Llm.VerifyNoOtherCalls();
    }

    [Test]
    public async Task AGoldSqlTheDataSourceRefuses_DidNotRun_AndLogsNeitherTheSqlNorTheError()
    {
        var harness = new Harness();
        harness.Provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult { Success = false, ErrorMessage = ProviderError });

        var replay = await harness.Service.ReplayGoldSqlAsync(DataSourceId, ProjectId, GoldSql, CancellationToken.None);

        replay.Should().Be(GoldSqlReplay.DidNotRun);
        harness.Logs.Entries.Should().NotBeEmpty();
        harness.Logs.Contains("orders").Should().BeFalse("neither the SQL nor the database's error is logged");
        harness.Logs.Contains("db-secret-host").Should().BeFalse();
        harness.ExecutionLog.Ends.Should().ContainSingle()
            .Which.Should().Match<SqlExecutionEnd>(x => x.Status == QueryExecutionLogStatus.Failed
                && !x.ErrorMessage!.Contains("db-secret-host")
                && !x.ErrorMessage.Contains("orders"));
    }

    // The gate refuses a statement that writes or reaches the system catalog: it never reaches the data source.
    [TestCase("DELETE FROM orders")]
    [TestCase("SELECT relname FROM pg_catalog.pg_class")]
    public async Task AGoldSqlTheGateRefuses_DidNotRun_AndNeverReachesTheDataSource(string sql)
    {
        var harness = new Harness();
        harness.Succeeds(Rows);

        var replay = await harness.Service.ReplayGoldSqlAsync(DataSourceId, ProjectId, sql, CancellationToken.None);

        replay.Should().Be(GoldSqlReplay.DidNotRun);
        harness.Provider.VerifyNoOtherCalls();
        harness.ExecutionLog.Starts.Should().BeEmpty("nothing reached the data source");
    }

    [Test]
    public async Task AGoldSqlTheRowSecurityDenies_DidNotRun_AndNeverReachesTheDataSource()
    {
        var resolver = new Mock<IRowSecurityResolver>();
        resolver
            .Setup(x => x.ResolveAsync(DataSourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(RowSecurityContext.Denied("no_entitlement"));
        var harness = new Harness(resolver.Object);
        harness.Succeeds(Rows);

        var replay = await harness.Service.ReplayGoldSqlAsync(DataSourceId, ProjectId, GoldSql, CancellationToken.None);

        replay.Should().Be(GoldSqlReplay.DidNotRun);
        resolver.Verify(x => x.ResolveAsync(DataSourceId, It.IsAny<CancellationToken>()), Times.Once);
        harness.Provider.VerifyNoOtherCalls();
    }

    // A failure of the data source (unreachable, disabled, a statement timeout) or a data source that is gone: the gold SQL
    // did not run, and the log carries the failure's code, never its message.
    [TestCase("throws")]
    [TestCase("times out")]
    [TestCase("is gone")]
    public async Task AGoldSqlWhoseDataSourceFails_DidNotRun_AndLogsNoMessage(string failure)
    {
        var harness = new Harness(dataSourceExists: failure != "is gone");
        harness.Provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure == "times out" ? new OperationCanceledException(ProviderError) : new InvalidOperationException(ProviderError));

        var replay = await harness.Service.ReplayGoldSqlAsync(DataSourceId, ProjectId, GoldSql, CancellationToken.None);

        replay.Should().Be(GoldSqlReplay.DidNotRun);
        harness.Logs.Entries.Should().ContainSingle();
        harness.Logs.Contains("db-secret-host").Should().BeFalse();
        harness.Logs.Contains("orders").Should().BeFalse();
        harness.Logs.Entries.Single().Exception.Should().BeNull("a failure is logged as its code, never the exception");
    }

    [Test]
    public async Task TheCallersCancellation_Propagates()
    {
        var harness = new Harness();
        using var cancellation = new CancellationTokenSource();
        harness.Provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .Returns<DataSource, string, Dictionary<string, object?>, CancellationToken>((_, _, _, ct) =>
            {
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();

                return Task.FromResult(new ProviderQueryResult { Success = true, Rows = Rows });
            });

        var act = () => harness.Service.ReplayGoldSqlAsync(DataSourceId, ProjectId, GoldSql, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        harness.ExecutionLog.Ends.Should().ContainSingle().Which.Status.Should().Be(QueryExecutionLogStatus.Cancelled);
    }

    // The replay runs under the project's effective settings (its row cap here), or the global ones without a project.
    [TestCase(ProjectId, "LIMIT 37")]
    [TestCase(null, "LIMIT 1000")]
    public async Task TheReplay_RunsUnderItsProjectsSettings_OrTheGlobalOnes(int? projectId, string rowCap)
    {
        var harness = new Harness(projectSettings: new McpSettingsData { MaxRowLimit = 37 });
        harness.Succeeds(Rows);

        await harness.Service.ReplayGoldSqlAsync(DataSourceId, projectId, GoldSql, CancellationToken.None);

        harness.Provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.Is<string>(y => y.Contains(rowCap)), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // The statement the gate wrote is in the execution log, as a gold SQL replay, before it is sent; its row is closed with
    // how it ended.
    [Test]
    public async Task AReplay_IsInTheExecutionLogBeforeItIsSent_AndClosedWithItsRows()
    {
        var harness = new Harness();
        harness.Succeeds(Rows);

        await harness.Service.ReplayGoldSqlAsync(DataSourceId, ProjectId, GoldSql, CancellationToken.None);

        harness.Order.Should().Equal("start", "execute", "end");
        var sent = harness.Provider.Invocations.Should().ContainSingle().Which.Arguments[1].Should().BeOfType<string>().Subject;
        harness.ExecutionLog.Starts.Should().ContainSingle()
            .Which.Should().Be(new SqlExecutionStart(DataSourceId, sent, SqlExecutionContexts.GoldSqlReplay));
        harness.ExecutionLog.Ends.Should().ContainSingle()
            .Which.Should().Match<SqlExecutionEnd>(x => x.Status == QueryExecutionLogStatus.Succeeded && x.ResultCount == Rows.Count);
    }

    // The row names the scope's caller — the person promoting — by Users.Id, never a name, and is written before the
    // statement is sent.
    [Test]
    public async Task TheReplaysExecutionLogRow_NamesTheCallerByUsersId_BeforeTheStatementIsSent()
    {
        var logged = new List<object>();
        var harness = new Harness(executionLog: RealExecutionLog(LogStore(logged), required: true, TestProjectAccess.Actors(beaconUserId: 7)));
        var rowsAtSend = new List<ManualQueryExecutionLog>();
        harness.Provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .Callback(() => rowsAtSend.AddRange(logged.OfType<ManualQueryExecutionLog>()))
            .ReturnsAsync(new ProviderQueryResult { Success = true, Rows = Rows });

        var replay = await harness.Service.ReplayGoldSqlAsync(DataSourceId, ProjectId, GoldSql, CancellationToken.None);

        replay.Ran.Should().BeTrue();
        var sent = harness.Provider.Invocations.Should().ContainSingle().Which.Arguments[1].Should().BeOfType<string>().Subject;
        var row = rowsAtSend.Should().ContainSingle().Which;
        row.TriggeredByUserId.Should().Be(7);
        row.UserId.Should().BeNull("the caller is recorded by Users.Id only");
        row.DataSourceId.Should().Be(DataSourceId);
        row.ExecutionContext.Should().Be(SqlExecutionContexts.GoldSqlReplay);
        row.QueryText.Should().Be(sent);
        row.Status.Should().Be(QueryExecutionLogStatus.Started);
    }

    [Test]
    public async Task WhenTheLogIsRequired_AReplayWhoseRowCannotBeWritten_DidNotRun_AndNeverReachesTheDataSource()
    {
        var harness = new Harness(executionLog: RealExecutionLog(UnavailableLogStore(), required: true, TestProjectAccess.Actors(beaconUserId: 7)));
        harness.Succeeds(Rows);

        var replay = await harness.Service.ReplayGoldSqlAsync(DataSourceId, ProjectId, GoldSql, CancellationToken.None);

        replay.Should().Be(GoldSqlReplay.DidNotRun);
        harness.Provider.Invocations.Should().BeEmpty("a statement does not run without its execution log row while the log is required");
        harness.Logs.Entries.Should().ContainSingle().Which.Exception.Should().BeNull("a failure is logged as its code, never the exception");
        harness.Logs.Contains("orders").Should().BeFalse();
    }

    /// <summary>The execution log Beacon registers, over <paramref name="store"/>, naming <paramref name="actors"/>' caller.</summary>
    internal static SqlExecutionAudit RealExecutionLog(IDbContextFactory<BeaconContext> store, bool required, IBeaconActorAccessor actors)
    {
        return new SqlExecutionAudit(
            store,
            actors,
            Options.Create(new SecurityAuditOptions { ExecutionLog = new ExecutionLogOptions { Required = required } }),
            NullLogger<SqlExecutionAudit>.Instance);
    }

    /// <summary>An execution log store that cannot be reached.</summary>
    internal static IDbContextFactory<BeaconContext> UnavailableLogStore()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the log store is unavailable"));

        return factory.Object;
    }

    // An execution log store whose saved rows land in saved.
    private static IDbContextFactory<BeaconContext> LogStore(List<object> saved)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(new Dictionary<Type, object>(), saved));

        return factory.Object;
    }

    /// <summary>
    /// The real eval service over one PostgreSQL data source, a scripted provider, an LLM nothing may call and an execution
    /// log (by default one that records each start and end into <see cref="Order"/>).
    /// </summary>
    internal sealed class Harness
    {
        public Harness(
            IRowSecurityResolver? rowSecurity = null,
            bool dataSourceExists = true,
            McpSettingsData? projectSettings = null,
            ISqlExecutionAudit? executionLog = null,
            int dataSourceId = DataSourceId)
        {
            ExecutionLog = new RecordingExecutionAudit(Order);
            var sources = new List<DataSource>();
            if (dataSourceExists)
            {
                sources.Add(new DataSource
                {
                    Id = dataSourceId,
                    Name = "sales",
                    DataSourceType = DataSourceType.Database,
                    DatabaseEngineType = DatabaseEngineType.PostgreSQL,
                    EncryptedConnectionData = "encrypted"
                });
            }

            var saved = new List<object>();
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(
                    new Dictionary<Type, object>
                    {
                        [typeof(DataSource)] = RecordingBeaconContext.MemorySet(sources, saved)
                    },
                    saved));

            var providerFactory = new Mock<IDataSourceProviderFactory>();
            providerFactory
                .Setup(x => x.GetProvider(DataSourceType.Database))
                .Returns(Provider.Object);

            var settings = SettingsProviderMock.Create(
                new McpSettingsData { MaxRowLimit = 1000 },
                projectSettings: projectSettings == null ? null : new Dictionary<int, McpSettingsData> { [ProjectId] = projectSettings });
            var gate = TestSqlGate.Create();
            var pipeline = new AskSqlPipeline(
                Mock.Of<IKnowledgeGraphService>(MockBehavior.Strict),
                Mock.Of<ISqlGenerationService>(MockBehavior.Strict),
                gate,
                TestRowSecurity.Resolver,
                TestRowDataEgress.Discarding(),
                Options.Create(new McpDeploymentOptions()),
                new McpStatementLog(),
                NullLogger<AskSqlPipeline>.Instance);

            Service = new McpEvalService(
                factory.Object,
                pipeline,
                providerFactory.Object,
                new QueryGuardrailService(),
                gate,
                TestConfiguredDatabases.None,
                rowSecurity ?? TestRowSecurity.Resolver,
                settings.Object,
                Llm.Object,
                TestRowDataEgress.Discarding(),
                Logs.For<McpEvalService>(),
                executionLog ?? ExecutionLog);
        }

        public McpEvalService Service { get; }

        public Mock<IDataSourceProvider> Provider { get; } = new();

        public Mock<ILlmProvider> Llm { get; } = new(MockBehavior.Strict);

        public LogRecorder Logs { get; } = new();

        /// <summary>"start", "execute" and "end", in the order the default execution log and the provider saw them.</summary>
        public List<string> Order { get; } = [];

        /// <summary>The default execution log, used when none is passed in.</summary>
        public RecordingExecutionAudit ExecutionLog { get; }

        public void Succeeds(List<Dictionary<string, object?>> rows)
        {
            Provider
                .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
                .Callback(() => Order.Add("execute"))
                .ReturnsAsync(new ProviderQueryResult { Success = true, Rows = rows });
        }
    }
}

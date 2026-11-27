using Beacon.AI.Services.Eval;
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
/// run", logged by ids and failure codes only (§1.11); only the caller's cancellation propagates. The real
/// <see cref="McpEvalService"/> over in-memory sets, no database (§4.7).
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

    /// <summary>The real eval service over one PostgreSQL data source, a scripted provider and an LLM nothing may call.</summary>
    private sealed class Harness
    {
        public Harness(IRowSecurityResolver? rowSecurity = null, bool dataSourceExists = true, McpSettingsData? projectSettings = null)
        {
            var sources = new List<DataSource>();
            if (dataSourceExists)
            {
                sources.Add(new DataSource
                {
                    Id = DataSourceId,
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
                Logs.For<McpEvalService>());
        }

        public McpEvalService Service { get; }

        public Mock<IDataSourceProvider> Provider { get; } = new();

        public Mock<ILlmProvider> Llm { get; } = new(MockBehavior.Strict);

        public LogRecorder Logs { get; } = new();

        public void Succeeds(List<Dictionary<string, object?>> rows)
        {
            Provider
                .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProviderQueryResult { Success = true, Rows = rows });
        }
    }
}

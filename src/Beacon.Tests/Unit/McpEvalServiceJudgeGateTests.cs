using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Eval;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Ai;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC7 — the LLM-as-judge is a PII-exposing path (it renders result-set rows into a prompt), so it MUST
/// stay off unless explicitly enabled. These tests drive the real <see cref="McpEvalService.RunAsync"/>
/// against a mocked <see cref="BeaconContext"/> (async-queryable doubles, no DB — §4.7) with a golden case
/// whose gold and generated result sets DIFFER (so the judge's <c>!passed</c> precondition holds). SQL
/// generation is mocked, so <see cref="ILlmProvider"/> is reachable ONLY through the judge — making its
/// call count a precise proof of the gate. With <c>EnableEvalJudge=false</c> the provider is never touched
/// (no row data leaves the process); with it true the judge runs exactly once.
/// </summary>
[TestFixture]
public class McpEvalServiceJudgeGateTests
{
    private const int RunId = 100;
    private const int CaseId = 7;
    private const int DataSourceId = 1;

    [Test]
    public async Task RunAsync_JudgeDisabled_NeverInvokesLlmProvider_EvenWhenResultsDiffer()
    {
        var llm = new Mock<ILlmProvider>();
        llm.Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = "EQUIVALENT" });

        var captured = new List<McpEvalResult>();
        var (service, run, _) = BuildService(llm, judgeEnabled: false, capturedResults: captured);

        await service.RunAsync(RunId, CancellationToken.None);

        // The gate: with the judge disabled the LLM provider is NEVER called, so no result-set rows are
        // ever rendered into a prompt and sent out of process (§1.6/§1.11).
        llm.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        captured.Should().ContainSingle();
        captured[0].JudgeUsed.Should().BeFalse();
        captured[0].Passed.Should().BeFalse("the gold and generated result sets differ");
        run.Status.Should().Be("Completed");
    }

    [Test]
    public async Task RunAsync_JudgeEnabled_InvokesLlmProviderOnceForDifferingResults()
    {
        var llm = new Mock<ILlmProvider>();
        llm.Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = "EQUIVALENT" });

        var captured = new List<McpEvalResult>();
        var (service, _, _) = BuildService(llm, judgeEnabled: true, capturedResults: captured);

        await service.RunAsync(RunId, CancellationToken.None);

        llm.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Once);

        captured.Should().ContainSingle();
        captured[0].JudgeUsed.Should().BeTrue();
        captured[0].JudgeVerdict.Should().Be("EQUIVALENT");
    }

    [Test]
    public async Task RunAsync_MutatingGoldSql_IsNeverExecutedAgainstTheDataSource()
    {
        // A golden case whose GoldSql is a mutating statement (e.g. a bad promotion) MUST be blocked by the
        // eval service's own read-only gate (SqlReadOnlyAstValidator, applied inside ExecuteReadOnlyAsync)
        // BEFORE it can reach the provider — read-only is enforced here, not inherited (§1.5, lesson 2026-07-03).
        // The guardrail is mocked to pass, so the REAL AST validator is proven to be the blocking gate.
        var llm = new Mock<ILlmProvider>();
        llm.Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = "EQUIVALENT" });

        var captured = new List<McpEvalResult>();
        var (service, _, provider) = BuildService(
            llm, judgeEnabled: false, capturedResults: captured, goldSql: "DELETE FROM orders");

        await service.RunAsync(RunId, CancellationToken.None);

        // The mutating gold SQL is never handed to the provider for execution.
        provider.Verify(
            x => x.ExecuteQueryAsync(
                It.IsAny<DataSource>(),
                It.Is<string>(s => s.Contains("DELETE")),
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        captured.Should().ContainSingle();
        captured[0].Passed.Should().BeFalse();
        captured[0].FailureTag.Should().Be(McpEvalFailureTag.ExecutionError,
            "the gold SQL was rejected read-only before execution, so the case could not be scored as passing");
    }

    private static (McpEvalService Service, McpEvalRun Run, Mock<IDataSourceProvider> Provider) BuildService(
        Mock<ILlmProvider> llm, bool judgeEnabled, List<McpEvalResult> capturedResults, string goldSql = "SELECT 1 AS n")
    {
        var run = new McpEvalRun { Id = RunId, ProjectId = 1, Status = "Running" };
        var evalCase = new McpEvalCase
        {
            Id = CaseId,
            ProjectId = 1,
            DataSourceId = DataSourceId,
            Question = "how many orders were placed?",
            GoldSql = goldSql,
            IsActive = true
        };
        var dataSource = new DataSource
        {
            Id = DataSourceId,
            Name = "ds",
            DataSourceType = DataSourceType.Database,
            EncryptedConnectionData = "encrypted",
            DatabaseEngineType = DatabaseEngineType.PostgreSQL
        };

        var context = new EvalTestContext(
            BuildDbSet(new[] { run }).Object,
            BuildDbSet(new[] { evalCase }).Object,
            BuildDbSet(new[] { dataSource }).Object,
            BuildResultsDbSet(capturedResults).Object);

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        var knowledge = new Mock<IKnowledgeGraphService>();
        knowledge
            .Setup(x => x.GetSmartContextForAskAsync(DataSourceId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SmartSchemaContext { FullContext = "schema", DatabaseDialect = "PostgreSql" });

        // Generation is mocked so the ONLY path that can reach ILlmProvider is the judge.
        var sqlGen = new Mock<ISqlGenerationService>();
        sqlGen
            .Setup(x => x.GenerateAsync(
                It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult("SELECT 2 AS n", ["orders"]));

        // Provider returns DIFFERENT rows for gold ("SELECT 1") vs generated ("SELECT 2") — both succeed,
        // so fingerprints differ, the case does not pass, and the judge's precondition is satisfied.
        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteQueryAsync(
                It.IsAny<DataSource>(), It.Is<string>(s => s.Contains("SELECT 1")),
                It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult
            {
                Success = true,
                Rows = [new Dictionary<string, object?> { ["n"] = 1 }]
            });
        provider
            .Setup(x => x.ExecuteQueryAsync(
                It.IsAny<DataSource>(), It.Is<string>(s => s.Contains("SELECT 2")),
                It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult
            {
                Success = true,
                Rows = [new Dictionary<string, object?> { ["n"] = 2 }]
            });

        var providerFactory = new Mock<IDataSourceProviderFactory>();
        providerFactory
            .Setup(x => x.GetProvider(DataSourceType.Database))
            .Returns(provider.Object);

        var guardrail = new Mock<IQueryGuardrailService>();
        guardrail
            .Setup(x => x.ValidateQuery(It.IsAny<string>(), It.IsAny<QueryGuardrailOptions>()))
            .Returns(new Beacon.Core.Services.Security.QueryValidationResult(true));
        guardrail
            .Setup(x => x.ApplyRowLimit(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>()))
            .Returns<string, int, string?>((sql, _, _) => sql);
        guardrail
            .Setup(x => x.IsPiiColumn(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>()))
            .Returns(false);

        var settings = new Mock<IMcpSettingsProvider>();
        settings
            .Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpSettingsData
            {
                EnableEvalJudge = judgeEnabled,
                EnablePiiDetection = false,
                MaxRowLimit = 1000
            });

        var service = new McpEvalService(
            factory.Object,
            knowledge.Object,
            sqlGen.Object,
            providerFactory.Object,
            guardrail.Object,
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            settings.Object,
            llm.Object,
            NullLogger<McpEvalService>.Instance);

        return (service, run, provider);
    }

    private static Mock<DbSet<T>> BuildDbSet<T>(IEnumerable<T> data) where T : class
    {
        var queryable = data.AsQueryable();
        var set = new Mock<DbSet<T>>();
        set.As<IAsyncEnumerable<T>>()
            .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(() => new TestAsyncEnumerator<T>(data.GetEnumerator()));
        set.As<IQueryable<T>>().Setup(x => x.Provider).Returns(new TestAsyncQueryProvider<T>(queryable.Provider));
        set.As<IQueryable<T>>().Setup(x => x.Expression).Returns(queryable.Expression);
        set.As<IQueryable<T>>().Setup(x => x.ElementType).Returns(queryable.ElementType);
        set.As<IQueryable<T>>().Setup(x => x.GetEnumerator()).Returns(() => data.GetEnumerator());
        return set;
    }

    // Captures every Add so the persisted per-case result can be asserted without a real SaveChanges.
    private static Mock<DbSet<McpEvalResult>> BuildResultsDbSet(List<McpEvalResult> captured)
    {
        var set = BuildDbSet(captured);
        set.Setup(x => x.Add(It.IsAny<McpEvalResult>()))
            .Callback<McpEvalResult>(captured.Add);
        return set;
    }

    /// <summary>
    /// A <see cref="BeaconContext"/> whose eval + data-source sets resolve to the supplied mocked sets.
    /// <c>RunAsync</c> never touches <c>Database</c>, so no provider spoofing is needed. SaveChanges is a no-op.
    /// </summary>
    private sealed class EvalTestContext : BeaconContext
    {
        private static readonly DbContextOptions<EvalTestContext> Options =
            new DbContextOptionsBuilder<EvalTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly DbSet<McpEvalRun> _runs;
        private readonly DbSet<McpEvalCase> _cases;
        private readonly DbSet<DataSource> _dataSources;
        private readonly DbSet<McpEvalResult> _results;

        public EvalTestContext(
            DbSet<McpEvalRun> runs,
            DbSet<McpEvalCase> cases,
            DbSet<DataSource> dataSources,
            DbSet<McpEvalResult> results)
            : base(Options, "beacon")
        {
            _runs = runs;
            _cases = cases;
            _dataSources = dataSources;
            _results = results;
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpEvalRun))
            {
                return (DbSet<TEntity>)(object)_runs;
            }

            if (typeof(TEntity) == typeof(McpEvalCase))
            {
                return (DbSet<TEntity>)(object)_cases;
            }

            if (typeof(TEntity) == typeof(DataSource))
            {
                return (DbSet<TEntity>)(object)_dataSources;
            }

            if (typeof(TEntity) == typeof(McpEvalResult))
            {
                return (DbSet<TEntity>)(object)_results;
            }

            return base.Set<TEntity>();
        }

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}

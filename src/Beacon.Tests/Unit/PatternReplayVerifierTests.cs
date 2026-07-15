using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Eval;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Learning;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// The replay-verification gate (§ Architecture ⑥) promotes a candidate ONLY when injecting it flips
/// ≥ minFlips of its relevant failing golden cases to passing with ZERO relevant regressions. These tests
/// drive the real <see cref="PatternReplayVerifier"/> with a scripted <see cref="IMcpEvalService"/>
/// (baseline vs candidate keyed by whether the extra-context lesson block is present) over a mocked
/// <see cref="BeaconContext"/> (async-queryable doubles, no DB — §4.7). A final security test proves the
/// read-only gate is preserved through the eval refactor: a mutating gold SQL never reaches the provider.
/// </summary>
[TestFixture]
public class PatternReplayVerifierTests
{
    private const int DataSourceId = 1;

    [Test]
    public async Task VerifyAsync_FlipsOneFailingCase_NoRegressions_Passes()
    {
        var candidate = BuildCandidate();
        var eval = new Mock<IMcpEvalService>();

        // q1 is a relevant failing case (gold SQL references public.loans): baseline FAILS, injecting the
        // candidate lesson makes it PASS — a flip with no regression.
        ScriptCase(eval, question: "q1", baselinePass: false, candidatePass: true);

        var cases = new[]
        {
            EvalCase("q1", goldSql: "SELECT id FROM public.loans WHERE created_time > now()")
        };

        var verifier = BuildVerifier(eval, cases);

        var verdict = await verifier.VerifyAsync(candidate, minFlips: 1, CancellationToken.None);

        verdict.RelevantCases.Should().Be(1);
        verdict.BaselineFailing.Should().Be(1);
        verdict.Flipped.Should().Be(1);
        verdict.Regressions.Should().Be(0);
        verdict.Passed.Should().BeTrue();
    }

    [Test]
    public async Task VerifyAsync_InjectsCandidatesOwnLessonText_IntoCandidatePassOnly()
    {
        // F4: the candidate pass must inject THIS candidate's OWN lesson text — its PatternContent, plus its
        // ExampleQuestion/ExampleSql when present — not merely "some non-null block". The baseline pass gets
        // no extra context (null). This proves the replay actually evaluates the candidate under test rather
        // than an empty/placeholder lesson.
        var candidate = BuildCandidate();
        var eval = new Mock<IMcpEvalService>();

        ScriptCase(eval, question: "q1", baselinePass: false, candidatePass: true);

        var cases = new[]
        {
            EvalCase("q1", goldSql: "SELECT id FROM public.loans WHERE created_time > now()")
        };

        var verifier = BuildVerifier(eval, cases);

        await verifier.VerifyAsync(candidate, minFlips: 1, CancellationToken.None);

        // Candidate pass: extraContext carries the candidate's OWN content + example question + example SQL.
        eval.Verify(
            x => x.EvaluateCasePassesAsync(
                DataSourceId, "q1", It.IsAny<string>(), It.IsAny<string?>(),
                It.Is<string?>(s => s != null
                    && s.Contains(candidate.PatternContent)
                    && s.Contains(candidate.ExampleQuestion!)
                    && s.Contains(candidate.ExampleSql!)),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Baseline pass: NO extra context — the null-vs-injected distinction is preserved.
        eval.Verify(
            x => x.EvaluateCasePassesAsync(
                DataSourceId, "q1", It.IsAny<string>(), It.IsAny<string?>(),
                It.Is<string?>(s => s == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task VerifyAsync_FlipsOneButRegressesAnother_FailsOnRegression()
    {
        var candidate = BuildCandidate();
        var eval = new Mock<IMcpEvalService>();

        // q1 flips fail→pass; q2 regresses pass→fail. Any regression among relevant cases fails the gate,
        // even though a flip occurred.
        ScriptCase(eval, question: "q1", baselinePass: false, candidatePass: true);
        ScriptCase(eval, question: "q2", baselinePass: true, candidatePass: false);

        var cases = new[]
        {
            EvalCase("q1", goldSql: "SELECT id FROM public.loans"),
            EvalCase("q2", goldSql: "SELECT count(*) FROM public.loans")
        };

        var verifier = BuildVerifier(eval, cases);

        var verdict = await verifier.VerifyAsync(candidate, minFlips: 1, CancellationToken.None);

        verdict.RelevantCases.Should().Be(2);
        verdict.Flipped.Should().Be(1);
        verdict.Regressions.Should().Be(1);
        verdict.Passed.Should().BeFalse("a single relevant regression blocks promotion");
    }

    [Test]
    public async Task VerifyAsync_NoRelevantGoldenCases_ReturnsUnpromoted()
    {
        var candidate = BuildCandidate();
        var eval = new Mock<IMcpEvalService>();

        // The only active case for this data source references a DIFFERENT table, so it is not relevant to
        // the candidate (which is scoped to public.loans). No golden evidence → never auto-approve.
        var cases = new[]
        {
            EvalCase("qX", goldSql: "SELECT * FROM public.customers")
        };

        var verifier = BuildVerifier(eval, cases);

        var verdict = await verifier.VerifyAsync(candidate, minFlips: 1, CancellationToken.None);

        verdict.RelevantCases.Should().Be(0);
        verdict.Flipped.Should().Be(0);
        verdict.Passed.Should().BeFalse();

        // With no relevant cases the verifier must not attempt any generation/execution at all.
        eval.Verify(
            x => x.EvaluateCasePassesAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task VerifyAsync_UnmeasurableCase_CountsAsErrored_NotFlipNotRegression()
    {
        var candidate = BuildCandidate();
        var eval = new Mock<IMcpEvalService>();

        // q1 WOULD look like a flip (baseline fail → candidate pass), but neither pass was measurable (gold or
        // generated failed to execute — an infra/guardrail failure, not a clean result). It must be counted as
        // Errored and excluded from both the flip and the regression tallies, so it can neither promote junk
        // nor block a good pattern.
        ScriptCase(eval, question: "q1", baselinePass: false, candidatePass: true, measurable: false);

        var cases = new[]
        {
            EvalCase("q1", goldSql: "SELECT id FROM public.loans WHERE created_time > now()")
        };

        var verifier = BuildVerifier(eval, cases);

        var verdict = await verifier.VerifyAsync(candidate, minFlips: 1, CancellationToken.None);

        verdict.RelevantCases.Should().Be(1);
        verdict.Errored.Should().Be(1, "an unmeasurable case is errored, not measured");
        verdict.Flipped.Should().Be(0, "an unmeasurable case can never count as a flip");
        verdict.Regressions.Should().Be(0, "an unmeasurable case can never count as a regression");
        verdict.BaselineFailing.Should().Be(0, "an unmeasurable baseline pass is not a measured failure");
        verdict.Passed.Should().BeFalse();
    }

    [Test]
    public async Task VerifyAsync_AllCasesErrored_DoesNotPass_EvenWhenItWouldOtherwiseLookClean()
    {
        var candidate = BuildCandidate();
        var eval = new Mock<IMcpEvalService>();

        // The only relevant case is unmeasurable → Errored. With minFlips 0 the verdict would otherwise look
        // "clean" (Flipped 0 ≥ 0, Regressions 0) and be byte-identical to "baseline already passes everything".
        // The measured-at-least-one guard blocks that: an all-errored run must NOT pass.
        ScriptCase(eval, question: "q1", baselinePass: true, candidatePass: true, measurable: false);

        var cases = new[]
        {
            EvalCase("q1", goldSql: "SELECT id FROM public.loans")
        };

        var verifier = BuildVerifier(eval, cases);

        var verdict = await verifier.VerifyAsync(candidate, minFlips: 0, CancellationToken.None);

        verdict.RelevantCases.Should().Be(1);
        verdict.Errored.Should().Be(1);
        verdict.Flipped.Should().Be(0);
        verdict.Regressions.Should().Be(0);
        verdict.Passed.Should().BeFalse("an all-errored run measured nothing, so it cannot pass even at minFlips 0");
    }

    [Test]
    public async Task EvaluateCasePassesAsync_MutatingGoldSql_NeverReachesTheProvider()
    {
        // SECURITY (§1.5, lesson 2026-07-03): the replay gate's only execution entry point is
        // McpEvalService.EvaluateCasePassesAsync, which reuses ExecuteReadOnlyAsync (SqlReadOnlyAstValidator
        // + guardrail, ReadOnly forced true). A mutating gold SQL must be rejected BEFORE the provider runs.
        // The guardrail is mocked to pass so the REAL AST validator is proven to be the blocking gate.
        var llm = new Mock<ILlmProvider>();
        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteQueryAsync(
                It.IsAny<DataSource>(), It.IsAny<string>(),
                It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult
            {
                Success = true,
                Rows = [new Dictionary<string, object?> { ["n"] = 1 }]
            });

        var service = BuildEvalService(llm, provider, generatedSql: "SELECT 1 AS n");

        var evaluation = await service.EvaluateCasePassesAsync(
            DataSourceId, "how many orders?", goldSql: "DELETE FROM orders", goldResultFingerprint: null,
            extraContext: null, CancellationToken.None);

        // The mutating gold SQL is never handed to the provider for execution.
        provider.Verify(
            x => x.ExecuteQueryAsync(
                It.IsAny<DataSource>(),
                It.Is<string>(s => s.Contains("DELETE")),
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        // Gold could not execute (rejected) and has no frozen fingerprint, so the case cannot pass — and
        // because gold never executed the outcome is not even measurable (the replay gate would count it as
        // errored, never a clean fail eligible to flip).
        evaluation.Passed.Should().BeFalse();
        evaluation.Measurable.Should().BeFalse();
    }

    private static McpLearnedPattern BuildCandidate()
    {
        return new McpLearnedPattern
        {
            Id = 55,
            ProjectId = 1,
            DataSourceId = DataSourceId,
            SchemaName = "public",
            TableName = "loans",
            PatternType = McpPatternType.SchemaCorrection,
            PatternContent = "NEVER use 'created_at' on public.loans — correct column is 'created_time'",
            ExampleQuestion = "loans created this month",
            ExampleSql = "SELECT * FROM public.loans WHERE created_time >= date_trunc('month', now())",
            Status = McpPatternStatus.NeedsEvidence
        };
    }

    private static McpEvalCase EvalCase(string question, string goldSql)
    {
        return new McpEvalCase
        {
            Id = question.GetHashCode() & 0x7fffffff,
            ProjectId = 1,
            DataSourceId = DataSourceId,
            Question = question,
            GoldSql = goldSql,
            IsActive = true
        };
    }

    // Scripts the eval probe for one question: baseline pass (no lesson injected, extraContext == null) and
    // candidate pass (lesson injected, extraContext != null) are returned independently. When
    // <paramref name="measurable"/> is false BOTH passes report an unmeasurable outcome (gold/generated did
    // not both execute) — the verifier must then count the case as Errored, never a flip or a regression.
    private static void ScriptCase(
        Mock<IMcpEvalService> eval, string question, bool baselinePass, bool candidatePass, bool measurable = true)
    {
        eval.Setup(x => x.EvaluateCasePassesAsync(
                DataSourceId, question, It.IsAny<string>(), It.IsAny<string?>(),
                It.Is<string?>(s => s == null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CaseEvaluation(baselinePass, measurable));
        eval.Setup(x => x.EvaluateCasePassesAsync(
                DataSourceId, question, It.IsAny<string>(), It.IsAny<string?>(),
                It.Is<string?>(s => s != null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CaseEvaluation(candidatePass, measurable));
    }

    private static PatternReplayVerifier BuildVerifier(Mock<IMcpEvalService> eval, McpEvalCase[] cases)
    {
        var context = new ReplayTestContext(evalCases: BuildDbSet(cases).Object);
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        return new PatternReplayVerifier(factory.Object, eval.Object, NullLogger<PatternReplayVerifier>.Instance);
    }

    private static McpEvalService BuildEvalService(
        Mock<ILlmProvider> llm, Mock<IDataSourceProvider> provider, string generatedSql)
    {
        var dataSource = new DataSource
        {
            Id = DataSourceId,
            Name = "ds",
            DataSourceType = DataSourceType.Database,
            EncryptedConnectionData = "encrypted",
            DatabaseEngineType = DatabaseEngineType.PostgreSQL
        };

        var context = new ReplayTestContext(dataSources: BuildDbSet(new[] { dataSource }).Object);
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        var knowledge = new Mock<IKnowledgeGraphService>();
        knowledge
            .Setup(x => x.GetSmartContextForAskAsync(DataSourceId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SmartSchemaContext { FullContext = "schema", DatabaseDialect = "PostgreSql" });

        var sqlGen = new Mock<ISqlGenerationService>();
        sqlGen
            .Setup(x => x.GenerateAsync(
                It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(generatedSql, ["orders"]));

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
                EnableEvalJudge = false,
                EnablePiiDetection = false,
                MaxRowLimit = 1000
            });

        return new McpEvalService(
            factory.Object,
            knowledge.Object,
            sqlGen.Object,
            providerFactory.Object,
            guardrail.Object,
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            settings.Object,
            llm.Object,
            NullLogger<McpEvalService>.Instance);
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

    /// <summary>
    /// A <see cref="BeaconContext"/> whose McpEvalCases and DataSources sets resolve to supplied mocked
    /// sets. SaveChanges is a no-op; the replay path never writes.
    /// </summary>
    private sealed class ReplayTestContext : BeaconContext
    {
        private static readonly DbContextOptions<ReplayTestContext> Options =
            new DbContextOptionsBuilder<ReplayTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly DbSet<McpEvalCase>? _evalCases;
        private readonly DbSet<DataSource>? _dataSources;

        public ReplayTestContext(DbSet<McpEvalCase>? evalCases = null, DbSet<DataSource>? dataSources = null)
            : base(Options, "beacon")
        {
            _evalCases = evalCases;
            _dataSources = dataSources;
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpEvalCase) && _evalCases != null)
            {
                return (DbSet<TEntity>)(object)_evalCases;
            }

            if (typeof(TEntity) == typeof(DataSource) && _dataSources != null)
            {
                return (DbSet<TEntity>)(object)_dataSources;
            }

            return base.Set<TEntity>();
        }

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}

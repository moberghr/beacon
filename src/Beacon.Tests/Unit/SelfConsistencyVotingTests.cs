using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Models;
using Beacon.Core.Models.Ai;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;

namespace Beacon.Tests.Unit;

[TestFixture]
public class SelfConsistencyVotingTests
{
    // ---- SelectMajority: pure selection logic (no LLM / no DB) ----

    [Test]
    public void SelectMajority_MajorityResultSetWins_OverMinority()
    {
        var candidates = new List<(string Sql, string Fingerprint, bool Ok)>
        {
            ("SELECT count(*) FROM orders", "fp-A", true),
            ("SELECT id FROM orders", "fp-B", true),
            ("SELECT count(1) FROM orders", "fp-A", true),
            ("SELECT * FROM orders", "fp-B", true),
            ("SELECT count(*) FROM public.orders", "fp-A", true)
        };

        // fp-A has 3 members, fp-B has 2 → the first-seen fp-A SQL wins.
        AskSqlPipeline.SelectMajority(candidates).Should().Be("SELECT count(*) FROM orders");
    }

    // ---- ResultFingerprint: order-independent so same-set candidates agree ----

    [Test]
    public void ResultFingerprint_SameRowsDifferentOrder_Match()
    {
        var ascending = new AskExecutionResult(
            "### Results (2 rows)\n| id |\n| --- |\n| 1 |\n| 2 |", null, 2, true);
        var descending = new AskExecutionResult(
            "### Results (2 rows)\n| id |\n| --- |\n| 2 |\n| 1 |", null, 2, true);

        // Same row SET in different order (no stable ORDER BY) must fingerprint identically so the two
        // candidates count as agreeing during self-consistency voting.
        AskSqlPipeline.ResultFingerprint(ascending)
            .Should().Be(AskSqlPipeline.ResultFingerprint(descending));
    }

    [Test]
    public void ResultFingerprint_RawRowsOnly_DifferentCellValuesWithSameRowCount_DoNotMatch()
    {
        // The eval executor formats nothing and exposes raw rows — voting must fingerprint the rows, not
        // just the row count, or every executed candidate with N rows would "agree" regardless of content.
        var one = new AskExecutionResult(null, null, 1, true, Rows: [new Dictionary<string, object?> { ["id"] = 1 }]);
        var nine = new AskExecutionResult(null, null, 1, true, Rows: [new Dictionary<string, object?> { ["id"] = 9 }]);

        AskSqlPipeline.ResultFingerprint(one)
            .Should().NotBe(AskSqlPipeline.ResultFingerprint(nine));
    }

    [Test]
    public void ResultFingerprint_RawRowsOnly_SameRowsDifferentOrder_Match()
    {
        var ascending = new AskExecutionResult(null, null, 2, true, Rows:
        [
            new Dictionary<string, object?> { ["id"] = 1 },
            new Dictionary<string, object?> { ["id"] = 2 }
        ]);
        var descending = new AskExecutionResult(null, null, 2, true, Rows:
        [
            new Dictionary<string, object?> { ["id"] = 2 },
            new Dictionary<string, object?> { ["id"] = 1 }
        ]);

        AskSqlPipeline.ResultFingerprint(ascending)
            .Should().Be(AskSqlPipeline.ResultFingerprint(descending));
    }

    [Test]
    public void ResultFingerprint_DifferentRows_DoNotMatch()
    {
        var one = new AskExecutionResult(
            "### Results (1 rows)\n| id |\n| --- |\n| 1 |", null, 1, true);
        var nine = new AskExecutionResult(
            "### Results (1 rows)\n| id |\n| --- |\n| 9 |", null, 1, true);

        AskSqlPipeline.ResultFingerprint(one)
            .Should().NotBe(AskSqlPipeline.ResultFingerprint(nine));
    }

    [Test]
    public void SelectMajority_Tie_BreaksToFirstSeenGroup()
    {
        var candidates = new List<(string Sql, string Fingerprint, bool Ok)>
        {
            ("SELECT b", "fp-B", true),
            ("SELECT a", "fp-A", true),
            ("SELECT b2", "fp-B", true),
            ("SELECT a2", "fp-A", true)
        };

        // Both groups have 2 members, and neither has a lint opinion (no lintCountBySql passed) →
        // fp-B was seen first, so its first SQL wins.
        AskSqlPipeline.SelectMajority(candidates).Should().Be("SELECT b");
    }

    [Test]
    public void SelectMajority_Tie_BreaksToFewerLintFindings_BeforeFirstSeen()
    {
        var candidates = new List<(string Sql, string Fingerprint, bool Ok)>
        {
            ("SELECT b", "fp-B", true),
            ("SELECT a", "fp-A", true),
            ("SELECT b2", "fp-B", true),
            ("SELECT a2", "fp-A", true)
        };

        // Both groups still have 2 members and fp-B is still seen first, but fp-A's representative
        // SQL lints cleaner (0 findings vs 3) — the lint tie-break outranks first-seen order.
        var lintCountBySql = new Dictionary<string, int>
        {
            ["SELECT b"] = 3,
            ["SELECT a"] = 0
        };

        AskSqlPipeline.SelectMajority(candidates, sql => lintCountBySql.GetValueOrDefault(sql, 0))
            .Should().Be("SELECT a");
    }

    [Test]
    public void SelectMajority_IgnoresFailedCandidates_WhenChoosingMajority()
    {
        var candidates = new List<(string Sql, string Fingerprint, bool Ok)>
        {
            // Three failed candidates that happen to share a fingerprint must NOT win.
            ("SELECT bad1", "fp-FAIL", false),
            ("SELECT bad2", "fp-FAIL", false),
            ("SELECT bad3", "fp-FAIL", false),
            ("SELECT ok", "fp-OK", true)
        };

        AskSqlPipeline.SelectMajority(candidates).Should().Be("SELECT ok");
    }

    [Test]
    public void SelectMajority_AllFailed_ReturnsNull()
    {
        var candidates = new List<(string Sql, string Fingerprint, bool Ok)>
        {
            ("SELECT a", "fp-A", false),
            ("SELECT b", "fp-B", false)
        };

        AskSqlPipeline.SelectMajority(candidates).Should().BeNull();
    }

    [Test]
    public void SelectMajority_Empty_ReturnsNull()
    {
        AskSqlPipeline.SelectMajority(new List<(string, string, bool)>()).Should().BeNull();
    }

    [Test]
    public void SelectMajority_SingleSuccessfulCandidate_ReturnsIt()
    {
        var candidates = new List<(string Sql, string Fingerprint, bool Ok)>
        {
            ("SELECT only", "fp-X", true)
        };

        AskSqlPipeline.SelectMajority(candidates).Should().Be("SELECT only");
    }

    // ---- SqlGenerationService.GenerateCandidatesAsync: concurrent dispatch (§6.1) ----

    [Test]
    public async Task GenerateCandidatesAsync_IssuesRequestsConcurrently_ViaTaskWhenAll()
    {
        var gate = new object();
        var concurrentCalls = 0;
        var maxConcurrentCalls = 0;

        var llmProvider = new Mock<ILlmProvider>();
        llmProvider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                lock (gate)
                {
                    concurrentCalls++;
                    maxConcurrentCalls = Math.Max(maxConcurrentCalls, concurrentCalls);
                }

                await Task.Delay(50);

                lock (gate)
                {
                    concurrentCalls--;
                }

                return new LlmResponse { Content = "SELECT 1" };
            });

        var service = new SqlGenerationService();
        var settings = new McpSettingsData();

        var results = await service.GenerateCandidatesAsync(
            llmProvider.Object, "schema-context", "question", settings, candidateCount: 4, temperature: 0.7m, CancellationToken.None);

        results.Should().HaveCount(4);
        // If the requests ran sequentially (one await at a time) this would never exceed 1 — proves
        // GenerateCandidatesAsync dispatches them concurrently via Task.WhenAll (§6.1: real concurrency
        // is bounded by LlmRequestQueue behind the injected provider, not by this loop).
        maxConcurrentCalls.Should().BeGreaterThan(1);
    }

    [Test]
    public async Task GenerateCandidatesAsync_DropsIndividualUnusableCandidates_KeepsTheRest()
    {
        var llmProvider = new Mock<ILlmProvider>();
        var callIndex = 0;
        llmProvider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                var index = Interlocked.Increment(ref callIndex);
                // Every second sample is truncated (unusable) and must be dropped individually rather
                // than failing the whole batch.
                var response = index % 2 == 0
                    ? new LlmResponse { Content = "SELECT 1", Truncated = true }
                    : new LlmResponse { Content = "SELECT 1" };
                return Task.FromResult(response);
            });

        var service = new SqlGenerationService();
        var settings = new McpSettingsData();

        var results = await service.GenerateCandidatesAsync(
            llmProvider.Object, "schema-context", "question", settings, candidateCount: 4, temperature: 0.7m, CancellationToken.None);

        results.Should().HaveCount(2);
        results.Should().OnlyContain(x => x.Sql == "SELECT 1");
    }

    // SF-1: a candidate whose CompleteAsync call itself throws (not just a truncated/SQL-less response)
    // must be dropped individually — one failing concurrent candidate can never fail the whole batch.
    [Test]
    public async Task GenerateCandidatesAsync_OneCandidateCompleteAsyncThrows_TheOthersStillReturn()
    {
        var llmProvider = new Mock<ILlmProvider>();
        var callIndex = 0;
        llmProvider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                var index = Interlocked.Increment(ref callIndex);
                if (index == 2)
                {
                    throw new HttpRequestException("connection reset");
                }

                return Task.FromResult(new LlmResponse { Content = "SELECT 1" });
            });

        var service = new SqlGenerationService();
        var settings = new McpSettingsData();

        var results = await service.GenerateCandidatesAsync(
            llmProvider.Object, "schema-context", "question", settings, candidateCount: 4, temperature: 0.7m, CancellationToken.None);

        results.Should().HaveCount(3, "one candidate's CompleteAsync call threw and must be dropped individually");
        results.Should().OnlyContain(x => x.Sql == "SELECT 1");
    }

    // ---- Flow: voting layered on top of the repair loop in AskSqlPipeline ----

    private const int DataSourceId = 7;
    private const int ProjectId = 42;
    private const string Question = "How many orders last week?";

    private const string WinnerSql = "SELECT count(*) FROM orders";
    private const string WinnerSqlB = "SELECT count(1) FROM orders";
    private const string WinnerSqlC = "SELECT count(*) FROM public.orders";
    private const string DifferSqlA = "SELECT id FROM orders";
    private const string DifferSqlB = "SELECT name FROM orders";
    private const string MutatingSql = "DELETE FROM orders";

    private static readonly List<string> TwoTables = ["orders", "order_items"];
    private static readonly List<string> OneTable = ["orders"];

    private Mock<IKnowledgeGraphService> _knowledgeGraph = null!;
    private Mock<IQueryGuardrailService> _guardrail = null!;
    private Mock<IAskSqlExecutor> _executor = null!;
    private Mock<ISqlGenerationService> _sqlGeneration = null!;
    private Mock<ILlmProvider> _llmProvider = null!;
    private McpSettingsData _settings = null!;

    [SetUp]
    public void SetUp()
    {
        _knowledgeGraph = new Mock<IKnowledgeGraphService>();
        _guardrail = new Mock<IQueryGuardrailService>();
        _executor = new Mock<IAskSqlExecutor>();
        _sqlGeneration = new Mock<ISqlGenerationService>();
        _llmProvider = new Mock<ILlmProvider>();
        _settings = new McpSettingsData { EnableSelfConsistency = true };

        _knowledgeGraph
            .Setup(x => x.GetSmartContextForAskAsync(DataSourceId, ProjectId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SmartSchemaContext
            {
                FullContext = "schema-context",
                DatabaseDialect = "PostgreSQL",
                TotalTableCount = 1
            });

        // Guardrail passes everything — the REAL SqlReadOnlyAstValidator is the read-only gate here.
        _guardrail
            .Setup(x => x.ValidateQuery(It.IsAny<string>(), It.IsAny<QueryGuardrailOptions>()))
            .Returns(new QueryValidationResult(true));

        // Clean dry-run so the repair loop does not fire on the winner.
        _executor
            .Setup(x => x.DryRunAsync(DataSourceId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
    }

    [Test]
    public async Task SingleTableCandidate_NeverRequestsAdditionalCandidates()
    {
        // R7: the single candidate references fewer than SelfConsistencyMinTables (default 2) tables,
        // so voting must not even be attempted.
        _sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), _settings, It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(WinnerSql, OneTable));
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, WinnerSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (1 rows)\n", null, 1, true));

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        outcome.Text.Should().NotContain("Self-consistency");
        _sqlGeneration.Verify(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>(), It.IsAny<decimal?>()), Times.Once);
        _sqlGeneration.Verify(x => x.GenerateCandidatesAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<McpSettingsData>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task MultiTableCandidate_RequestsExactlyNMinusOneExtraCandidates()
    {
        _settings.SelfConsistencyCandidateCount = 4;

        _sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), _settings, It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(WinnerSql, TwoTables));
        _sqlGeneration
            .Setup(x => x.GenerateCandidatesAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
                _settings, It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SqlGenerationResult>());
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, WinnerSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (1 rows)\n", null, 1, true));

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        // N (4) - 1 = 3 extra candidates requested.
        _sqlGeneration.Verify(x => x.GenerateCandidatesAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            _settings, 3, It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Voting_ChoosesMajorityResultSet_AndFeedsWinnerIntoRepairPath()
    {
        _settings.SelfConsistencyCandidateCount = 6;

        // The single low-temperature candidate (WinnerSql) is generated first, then 5 more are
        // requested concurrently. 3 candidates (including the single one) agree on result-set A, 2
        // differ, and 1 is a mutating query that must be filtered by ValidateGeneratedSql before it
        // can ever be executed.
        _sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), _settings, It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(WinnerSql, TwoTables));

        var extraCandidates = new List<SqlGenerationResult>
        {
            new(DifferSqlA, TwoTables),
            new(WinnerSqlB, TwoTables),
            new(DifferSqlB, TwoTables),
            new(WinnerSqlC, TwoTables),
            new(MutatingSql, TwoTables)
        };

        _sqlGeneration
            .Setup(x => x.GenerateCandidatesAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
                _settings, 5, It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(extraCandidates);

        // Result-set A (the three agreeing candidates share this exact fingerprint).
        var resultA = new AskExecutionResult("### Results (1 rows)\n| c | \n| 42 |\n", null, 1, true);
        foreach (var sql in new[] { WinnerSql, WinnerSqlB, WinnerSqlC })
        {
            _executor
                .Setup(x => x.ExecuteAsync(DataSourceId, sql, 100, It.IsAny<CancellationToken>()))
                .ReturnsAsync(resultA);
        }

        // Two divergent result sets (minority).
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, DifferSqlA, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (5 rows)\n", null, 5, true));
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, DifferSqlB, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (9 rows)\n", null, 9, true));

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        // Winner = first-seen member of the majority result-set group (the single candidate itself).
        outcome.Text.Should().Contain(WinnerSql);
        outcome.Text.Should().Contain("Self-consistency");
        outcome.Text.Should().Contain("### Results (1 rows)");

        // SECURITY: the mutating candidate must NEVER be executed — the AST gate drops it first.
        _executor.Verify(x => x.ExecuteAsync(DataSourceId, MutatingSql, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        // Valid candidates ARE executed read-only during voting.
        _executor.Verify(x => x.ExecuteAsync(DataSourceId, DifferSqlA, 100, It.IsAny<CancellationToken>()), Times.Once);
        // The single low-temperature candidate is ALWAYS generated, whether or not voting elects it.
        _sqlGeneration.Verify(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>(), It.IsAny<decimal?>()), Times.Once);
        // The winner is recorded as the generated SQL on the outcome.
        outcome.GeneratedSql.Should().Be(WinnerSql);
    }

    [Test]
    public async Task Voting_NoAgreement_FallsBackToSingleCandidateSql()
    {
        _settings.SelfConsistencyCandidateCount = 2;

        // Single candidate touches 2 tables (gate satisfied) but fails execution both during voting
        // and — since it becomes the fallback SQL unchanged — during the normal execution step below.
        _sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), _settings, It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(DifferSqlA, TwoTables));
        _sqlGeneration
            .Setup(x => x.GenerateCandidatesAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
                _settings, 1, It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SqlGenerationResult> { new(DifferSqlB, TwoTables) });
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, DifferSqlA, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult(null, "boom", 0, false));
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, DifferSqlB, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult(null, "boom2", 0, false));

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        // The no-winner outcome is surfaced, not silent (SF-F6): voting emits a note explaining it
        // produced no agreeing executable candidate, and the single candidate's own (also failing)
        // SQL proceeds through the ordinary repair path unchanged.
        outcome.Text.Should().Contain("Self-consistency");
        outcome.Text.Should().Contain("no candidate produced an executable, agreed result");
        outcome.GeneratedSql.Should().Be(DifferSqlA);
        outcome.Succeeded.Should().BeFalse();
        _sqlGeneration.Verify(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>(), It.IsAny<decimal?>()), Times.Once);
        _sqlGeneration.Verify(x => x.GenerateCandidatesAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<McpSettingsData>(), 1, It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Voting_Disabled_NeverGeneratesCandidates()
    {
        _settings.EnableSelfConsistency = false;
        _sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), _settings, It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(WinnerSql, TwoTables));
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, WinnerSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (1 rows)\n", null, 1, true));

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        outcome.Text.Should().NotContain("Self-consistency");
        _sqlGeneration.Verify(x => x.GenerateCandidatesAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<McpSettingsData>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Voting_SkippedWhenAllowSelfConsistencyIsFalse_EvenWhenSettingsEnableIt()
    {
        // The replay gate pins determinism by passing AllowSelfConsistency: false — voting must not run
        // even though the project setting enables it, execution is requested, and the candidate touches
        // enough tables to otherwise clear the gate.
        _sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), _settings, It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(WinnerSql, TwoTables));
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, WinnerSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (1 rows)\n", null, 1, true));

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object,
            new AskSqlPipelineOptions(AllowSelfConsistency: false), CancellationToken.None);

        outcome.Text.Should().NotContain("Self-consistency");
        _sqlGeneration.Verify(x => x.GenerateCandidatesAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<McpSettingsData>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Voting_WinnerCarriesAssumptions_FlowIntoOutcome()
    {
        // F001 regression: the elected winner's SqlGenerationResult — not just its SQL — must flow
        // into the outcome. The winner here is one of the EXTRA candidates (not the single one), so
        // this proves the fix is general, not a coincidence of the single candidate always winning.
        _settings.SelfConsistencyCandidateCount = 3;

        const string singleSql = "SELECT a FROM orders";
        const string extraSqlOne = "SELECT b FROM orders";
        const string extraSqlTwo = "SELECT c FROM orders";

        _sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), _settings, It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(singleSql, TwoTables, ["single assumption"]));
        _sqlGeneration
            .Setup(x => x.GenerateCandidatesAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
                _settings, 2, It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SqlGenerationResult>
            {
                new(extraSqlOne, TwoTables, ["extra assumption"], "which region?"),
                new(extraSqlTwo, TwoTables, ["irrelevant assumption"])
            });

        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, singleSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (1 rows)\n| c |\n| 1 |\n", null, 1, true));
        // extraSqlOne and extraSqlTwo agree on a DIFFERENT (larger) result set → majority group size 2
        // beats the single candidate's group size 1, and the first-seen member of that group (extraSqlOne)
        // wins, carrying its own assumptions and clarification hint.
        var majorityResult = new AskExecutionResult("### Results (2 rows)\n| c |\n| 1 |\n| 2 |\n", null, 2, true);
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, extraSqlOne, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(majorityResult);
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, extraSqlTwo, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(majorityResult);

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        outcome.GeneratedSql.Should().Be(extraSqlOne);
        outcome.Assumptions.Should().ContainSingle().Which.Should().Be("extra assumption");
        outcome.Assumptions.Should().NotContain("single assumption");
        outcome.ClarificationHint.Should().Be("which region?");
        outcome.Text.Should().Contain("### Assumptions");
        outcome.Text.Should().Contain("extra assumption");
        outcome.Text.Should().Contain("**Clarification suggested:** which region?");
    }

    // SF-1: the voting call itself (extra-candidate generation, here mocked to blow up as if it were an
    // unhandled provider failure) must be wrapped so any exception other than OperationCanceledException
    // logs a warning and falls back to the already-validated single candidate — never fails the ask.
    [Test]
    public async Task Voting_ExtraCandidateGenerationThrows_FallsBackToTheSingleCandidate()
    {
        _settings.SelfConsistencyCandidateCount = 3;

        _sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), _settings, It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(WinnerSql, TwoTables));
        _sqlGeneration
            .Setup(x => x.GenerateCandidatesAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
                _settings, 2, It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection reset"));

        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, WinnerSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (1 rows)\n", null, 1, true));

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        outcome.Succeeded.Should().BeTrue("voting blew up but the single candidate was already validated and executes fine");
        outcome.GeneratedSql.Should().Be(WinnerSql);
        outcome.Text.Should().NotContain("Self-consistency",
            "no voting note is rendered when voting itself threw rather than merely finding no agreement");
    }

    // TEST-5: full-pipeline tie broken by semantic lint findings (not just the pure SelectMajority unit
    // tests above) — two candidates land in their own singleton result-set group (a genuine SIZE tie),
    // one lint-clean and one flagged GROUP_BY_MISMATCH; the clean one must be elected.
    [Test]
    public async Task Voting_SizeTiedResultSets_ElectsTheLintCleanCandidate()
    {
        const string MismatchedSql = "SELECT o.customer_id, SUM(o.amount) FROM orders o";
        const string CleanSql = "SELECT o.customer_id, SUM(o.amount) FROM orders o GROUP BY o.customer_id";

        _settings.SelfConsistencyCandidateCount = 2;
        _settings.EnableSemanticLint = true;

        _sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), _settings, It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(MismatchedSql, TwoTables));
        _sqlGeneration
            .Setup(x => x.GenerateCandidatesAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
                _settings, 1, It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SqlGenerationResult> { new(CleanSql, TwoTables) });

        // Different row counts → different fingerprints → each candidate is its own singleton group,
        // a genuine SIZE tie (1 vs 1) rather than an agreement.
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, MismatchedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (1 rows)\n", null, 1, true));
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, CleanSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (2 rows)\n", null, 2, true));

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        outcome.GeneratedSql.Should().Be(CleanSql,
            "size-tied groups break to fewer lint findings — GROUP_BY_MISMATCH loses to the clean candidate");
        outcome.LintFindings.Should().BeEmpty();
        outcome.Text.Should().NotContain("Semantic warnings");
    }

    private AskSqlPipeline CreatePipeline()
    {
        return new AskSqlPipeline(
            _knowledgeGraph.Object,
            _sqlGeneration.Object,
            _guardrail.Object,
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            new SqlSchemaValidator(),
            new SqlSemanticLinter(),
            NullLogger<AskSqlPipeline>.Instance);
    }
}

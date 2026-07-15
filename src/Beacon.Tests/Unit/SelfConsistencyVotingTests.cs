using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Models;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;

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
        ProjectAskTool.SelectMajority(candidates).Should().Be("SELECT count(*) FROM orders");
    }

    // ---- ResultFingerprint: order-independent so same-set candidates agree ----

    [Test]
    public void ResultFingerprint_SameRowsDifferentOrder_Match()
    {
        var ascending = new QueryExecutionResult(
            "### Results (2 rows)\n| id |\n| --- |\n| 1 |\n| 2 |", null, 2, true);
        var descending = new QueryExecutionResult(
            "### Results (2 rows)\n| id |\n| --- |\n| 2 |\n| 1 |", null, 2, true);

        // Same row SET in different order (no stable ORDER BY) must fingerprint identically so the two
        // candidates count as agreeing during self-consistency voting.
        ProjectAskTool.ResultFingerprint(ascending)
            .Should().Be(ProjectAskTool.ResultFingerprint(descending));
    }

    [Test]
    public void ResultFingerprint_DifferentRows_DoNotMatch()
    {
        var one = new QueryExecutionResult(
            "### Results (1 rows)\n| id |\n| --- |\n| 1 |", null, 1, true);
        var nine = new QueryExecutionResult(
            "### Results (1 rows)\n| id |\n| --- |\n| 9 |", null, 1, true);

        ProjectAskTool.ResultFingerprint(one)
            .Should().NotBe(ProjectAskTool.ResultFingerprint(nine));
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

        // Both groups have 2 members; fp-B was seen first → its first SQL wins.
        ProjectAskTool.SelectMajority(candidates).Should().Be("SELECT b");
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

        ProjectAskTool.SelectMajority(candidates).Should().Be("SELECT ok");
    }

    [Test]
    public void SelectMajority_AllFailed_ReturnsNull()
    {
        var candidates = new List<(string Sql, string Fingerprint, bool Ok)>
        {
            ("SELECT a", "fp-A", false),
            ("SELECT b", "fp-B", false)
        };

        ProjectAskTool.SelectMajority(candidates).Should().BeNull();
    }

    [Test]
    public void SelectMajority_Empty_ReturnsNull()
    {
        ProjectAskTool.SelectMajority(new List<(string, string, bool)>()).Should().BeNull();
    }

    [Test]
    public void SelectMajority_SingleSuccessfulCandidate_ReturnsIt()
    {
        var candidates = new List<(string Sql, string Fingerprint, bool Ok)>
        {
            ("SELECT only", "fp-X", true)
        };

        ProjectAskTool.SelectMajority(candidates).Should().Be("SELECT only");
    }

    // ---- Flow: voting layered on top of the repair loop in ProjectAskTool ----

    private const int DataSourceId = 7;
    private const string Question = "How many orders last week?";

    private const string WinnerSql = "SELECT count(*) FROM orders";
    private const string WinnerSqlB = "SELECT count(1) FROM orders";
    private const string WinnerSqlC = "SELECT count(*) FROM public.orders";
    private const string DifferSqlA = "SELECT id FROM orders";
    private const string DifferSqlB = "SELECT name FROM orders";
    private const string MutatingSql = "DELETE FROM orders";

    private Mock<IKnowledgeGraphService> _knowledgeGraph = null!;
    private Mock<IQueryGuardrailService> _guardrail = null!;
    private Mock<IQueryExecutionService> _queryExecution = null!;
    private Mock<ISqlGenerationService> _sqlGeneration = null!;
    private Mock<ILlmProvider> _llmProvider = null!;
    private McpSettingsData _settings = null!;

    [SetUp]
    public void SetUp()
    {
        _knowledgeGraph = new Mock<IKnowledgeGraphService>();
        _guardrail = new Mock<IQueryGuardrailService>();
        _queryExecution = new Mock<IQueryExecutionService>();
        _sqlGeneration = new Mock<ISqlGenerationService>();
        _llmProvider = new Mock<ILlmProvider>();
        _settings = new McpSettingsData { EnableSelfConsistency = true };

        _knowledgeGraph
            .Setup(x => x.GetSmartContextForAskAsync(DataSourceId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        _queryExecution
            .Setup(x => x.ValidateAsync(DataSourceId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
    }

    [Test]
    public async Task Voting_ChoosesMajorityResultSet_AndFeedsWinnerIntoRepairPath()
    {
        // 3 candidates agree on result-set A, 2 differ, 1 is a mutating query that must be filtered
        // by ValidateGeneratedSql before it can ever be executed.
        var candidates = new List<SqlGenerationResult>
        {
            new(WinnerSql, ["orders"]),
            new(DifferSqlA, ["orders"]),
            new(WinnerSqlB, ["orders"]),
            new(DifferSqlB, ["orders"]),
            new(WinnerSqlC, ["orders"]),
            new(MutatingSql, ["orders"])
        };

        _sqlGeneration
            .Setup(x => x.GenerateCandidatesAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
                _settings, It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(candidates);

        // Result-set A (the three agreeing candidates share this exact fingerprint).
        var resultA = new QueryExecutionResult("### Results (1 rows)\n| c | \n| 42 |\n", null, 1, true);
        foreach (var sql in new[] { WinnerSql, WinnerSqlB, WinnerSqlC })
        {
            _queryExecution
                .Setup(x => x.ExecuteAsync(DataSourceId, sql, 100, It.IsAny<CancellationToken>()))
                .ReturnsAsync(resultA);
        }

        // Two divergent result sets (minority).
        _queryExecution
            .Setup(x => x.ExecuteAsync(DataSourceId, DifferSqlA, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionResult("### Results (5 rows)\n", null, 5, true));
        _queryExecution
            .Setup(x => x.ExecuteAsync(DataSourceId, DifferSqlB, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionResult("### Results (9 rows)\n", null, 9, true));

        var signal = new McpSignalBuilder();
        var (text, succeeded) = await CreateTool().GenerateAndExecuteSqlAsync(
            _llmProvider.Object, DataSourceId, Question, _settings, execute: true, signal, CancellationToken.None);

        succeeded.Should().BeTrue();
        // Winner = first-seen member of the majority result-set group.
        text.Should().Contain(WinnerSql);
        text.Should().Contain("Self-consistency");
        text.Should().Contain("### Results (1 rows)");

        // SECURITY: the mutating candidate must NEVER be executed — the AST gate drops it first.
        _queryExecution.Verify(x => x.ExecuteAsync(DataSourceId, MutatingSql, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        // Valid candidates ARE executed read-only during voting.
        _queryExecution.Verify(x => x.ExecuteAsync(DataSourceId, DifferSqlA, 100, It.IsAny<CancellationToken>()), Times.Once);
        // Voting produced a winner → the single-candidate GenerateAsync path is skipped entirely.
        _sqlGeneration.Verify(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>(), It.IsAny<decimal?>()), Times.Never);
        // The winner is recorded as the generated SQL on the signal.
        signal.Build().GeneratedSql.Should().Be(WinnerSql);
    }

    [Test]
    public async Task Voting_NoSuccessfulCandidate_FallsBackToSingleGenerationPath()
    {
        // Every candidate is valid read-only SQL but fails at execution → no majority result set.
        var candidates = new List<SqlGenerationResult>
        {
            new(DifferSqlA, ["orders"]),
            new(DifferSqlB, ["orders"])
        };

        _sqlGeneration
            .Setup(x => x.GenerateCandidatesAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
                _settings, It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(candidates);
        _queryExecution
            .Setup(x => x.ExecuteAsync(DataSourceId, It.Is<string>(s => s == DifferSqlA || s == DifferSqlB), 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionResult(null, "boom", 0, false));

        // Single-candidate fallback path.
        _sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), _settings, It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(WinnerSql, ["orders"]));
        _queryExecution
            .Setup(x => x.ExecuteAsync(DataSourceId, WinnerSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionResult("### Results (1 rows)\n", null, 1, true));

        var signal = new McpSignalBuilder();
        var (text, succeeded) = await CreateTool().GenerateAndExecuteSqlAsync(
            _llmProvider.Object, DataSourceId, Question, _settings, execute: true, signal, CancellationToken.None);

        succeeded.Should().BeTrue();
        // The no-winner fallback is surfaced, not silent (SF-F6): voting emits a note explaining it produced
        // nothing and the single-candidate path was used — while still returning the fallback result.
        text.Should().Contain("Self-consistency");
        text.Should().Contain("single-candidate generation");
        text.Should().Contain("### Results (1 rows)");
        _sqlGeneration.Verify(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>(), It.IsAny<decimal?>()), Times.Once);
    }

    [Test]
    public async Task Voting_Disabled_NeverGeneratesCandidates()
    {
        _settings.EnableSelfConsistency = false;
        _sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), _settings, It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(WinnerSql, ["orders"]));
        _queryExecution
            .Setup(x => x.ExecuteAsync(DataSourceId, WinnerSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionResult("### Results (1 rows)\n", null, 1, true));

        var signal = new McpSignalBuilder();
        var (text, _) = await CreateTool().GenerateAndExecuteSqlAsync(
            _llmProvider.Object, DataSourceId, Question, _settings, execute: true, signal, CancellationToken.None);

        text.Should().NotContain("Self-consistency");
        _sqlGeneration.Verify(x => x.GenerateCandidatesAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<McpSettingsData>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private ProjectAskTool CreateTool()
    {
        return new ProjectAskTool(
            _knowledgeGraph.Object,
            _guardrail.Object,
            settingsProvider: null!,
            serviceProvider: null!,
            projectContext: null!,
            sessionManager: null!,
            auditService: null!,
            signalService: null!,
            new SqlSchemaValidator(),
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            _queryExecution.Object,
            intentClassifier: null!,
            dataSourceRouter: null!,
            _sqlGeneration.Object,
            knowledgeAnswerService: null!,
            crossSourceQueryService: null!,
            NullLogger<ProjectAskTool>.Instance);
    }
}

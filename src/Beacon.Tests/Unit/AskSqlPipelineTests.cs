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
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// <see cref="AskSqlPipeline"/> is the shared generate → validate → repair → execute core run by BOTH the
/// MCP `ask` tool and <c>McpEvalService</c> (§ Architecture ①, SC1). These tests prove: (a) a case whose
/// first generation fails at execution but whose repair succeeds is scored PASS — the exact eval-parity
/// gap the pipeline extraction closes; (b) <c>Beacon.AI</c> carries no reference to <c>Beacon.MCP</c>
/// (§2.4/R2), which is what lets the eval harness run the identical pipeline without ever touching MCP.
/// </summary>
[TestFixture]
public class AskSqlPipelineTests
{
    private const int DataSourceId = 3;
    private const int ProjectId = 11;
    private const string Question = "total revenue by month";
    private const string GeneratedSql = "SELECT amount FROM public.sales";
    private const string RepairedSql = "SELECT total_amount FROM public.sales";

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
        _settings = new McpSettingsData();

        _knowledgeGraph
            .Setup(x => x.GetSmartContextForAskAsync(DataSourceId, ProjectId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SmartSchemaContext
            {
                FullContext = "schema-context",
                DatabaseDialect = "PostgreSQL",
                TotalTableCount = 1
            });

        _guardrail
            .Setup(x => x.ValidateQuery(It.IsAny<string>(), It.IsAny<QueryGuardrailOptions>()))
            .Returns(new QueryValidationResult(true));

        _executor
            .Setup(x => x.DryRunAsync(DataSourceId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
    }

    [Test]
    public async Task RunAsync_SchemaThenExecutionRepairSucceeds_IsScoredPass()
    {
        // The eval-parity gap this batch closes (SC1): a first generation that fails — here at execution,
        // the same way a golden case fails today when McpEvalService bypasses the repair loop — but whose
        // repair succeeds must be scored PASS, because the eval harness now runs the SAME AskSqlPipeline
        // instance type, with the SAME repair budget, as production `ask`.
        _sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), _settings, It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(GeneratedSql, ["sales"]));

        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult(null, "column \"amount\" does not exist", 0, false));

        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), GeneratedSql,
                "column \"amount\" does not exist", It.IsAny<string>(), It.IsAny<string?>(), Question, It.IsAny<CancellationToken>()))
            .ReturnsAsync(RepairedSql);

        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, RepairedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (12 rows)\n", null, 12, true,
                [new Dictionary<string, object?> { ["total_amount"] = 100 }]));

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        outcome.Succeeded.Should().BeTrue("the repair produced a valid, executable SQL — the case must be scored pass");
        outcome.Repairs.Should().ContainSingle();
        outcome.Repairs[0].Trigger.Should().Be("execution");
        outcome.Repairs[0].Succeeded.Should().BeTrue();
        outcome.FinalSql.Should().Be(RepairedSql);
        outcome.Execution.Should().NotBeNull();
        outcome.Execution!.Rows.Should().ContainSingle();
    }

    [Test]
    public async Task RunAsync_AssumptionsPresent_RendersAssumptionsSectionAndOutcomeField()
    {
        // Spec item 3 (SC3): assumptions parsed by SqlGenerationService must surface both in the
        // structured outcome AND in the rendered markdown transcript the ask tool sends verbatim.
        _sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), _settings, It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(
                GeneratedSql,
                ["sales"],
                ["\"last week\" = the 7 days before today", "revenue = SUM(amount)"]));

        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (1 row)\n", null, 1, true,
                [new Dictionary<string, object?> { ["amount"] = 100 }]));

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        outcome.Assumptions.Should().BeEquivalentTo(
        [
            "\"last week\" = the 7 days before today",
            "revenue = SUM(amount)"
        ]);
        outcome.ClarificationHint.Should().BeNull();
        outcome.Text.Should().Contain("### Assumptions");
        outcome.Text.Should().Contain("- \"last week\" = the 7 days before today");
        outcome.Text.Should().Contain("- revenue = SUM(amount)");
        outcome.Text.Should().NotContain("Clarification suggested");

        var sqlIndex = outcome.Text.IndexOf("### Generated SQL", StringComparison.Ordinal);
        var assumptionsIndex = outcome.Text.IndexOf("### Assumptions", StringComparison.Ordinal);
        assumptionsIndex.Should().BeGreaterThan(sqlIndex, "assumptions render after the Generated SQL block");
    }

    [Test]
    public async Task RunAsync_ClarificationHintPresent_RendersClarificationSuggestedLine()
    {
        _sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), _settings, It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(
                GeneratedSql,
                ["sales"],
                Assumptions: null,
                ClarificationHint: "Did you mean this quarter or last quarter?"));

        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (1 row)\n", null, 1, true,
                [new Dictionary<string, object?> { ["amount"] = 100 }]));

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        outcome.ClarificationHint.Should().Be("Did you mean this quarter or last quarter?");
        outcome.Assumptions.Should().BeEmpty();
        outcome.Text.Should().Contain("**Clarification suggested:** Did you mean this quarter or last quarter?");
        outcome.Text.Should().NotContain("### Assumptions");
    }

    [Test]
    public async Task RunAsync_NoAssumptionsOrClarification_OutcomeCarriesEmptyDefaultsAndNoExtraRendering()
    {
        // No assumptions/clarification block returned by generation == pre-change behaviour: the outcome
        // still exposes non-null, empty defaults and the transcript gains no extra section.
        _sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), _settings, It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(GeneratedSql, ["sales"]));

        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (1 row)\n", null, 1, true,
                [new Dictionary<string, object?> { ["amount"] = 100 }]));

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        outcome.Assumptions.Should().NotBeNull();
        outcome.Assumptions.Should().BeEmpty();
        outcome.ClarificationHint.Should().BeNull();
        outcome.Text.Should().NotContain("### Assumptions");
        outcome.Text.Should().NotContain("Clarification suggested");
    }

    [Test]
    public void BeaconAi_DoesNotReferenceBeaconMcp()
    {
        // §2.4 / R2: the pipeline returns a structured AskSqlOutcome specifically so Beacon.AI never needs
        // an McpSignalBuilder / SqlParsingHelper / ToolHelper dependency — proving the assembly graph never
        // grew a Beacon.MCP edge is what makes the eval harness safe to run without MCP in the picture.
        var referenced = typeof(AskSqlPipeline).Assembly.GetReferencedAssemblies();

        referenced.Should().NotContain(x => x.Name == "Beacon.MCP");
    }

    [Test]
    public async Task RunAsync_LintFindingsAndRepairReducesThem_ReplacesSqlAndRecordsLintRepair()
    {
        // R6/SC6: a GROUP BY mismatch (aggregate present, non-aggregated column missing from GROUP BY)
        // must spend exactly one repair attempt, and — because the repaired SQL lints strictly cleaner
        // AND still validates — the repaired SQL must replace the original.
        const string groupByMismatchSql = "SELECT o.customer_id, SUM(o.amount) AS total FROM public.orders o";
        const string repairedSql = "SELECT o.customer_id, SUM(o.amount) AS total FROM public.orders o GROUP BY o.customer_id";

        _sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), _settings, It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(groupByMismatchSql, ["orders"]));

        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), groupByMismatchSql,
                It.Is<string>(s => s.StartsWith("Semantic check:", StringComparison.Ordinal) && s.Contains("GROUP_BY_MISMATCH")),
                It.IsAny<string>(), It.IsAny<string?>(), Question, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repairedSql);

        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, repairedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (1 row)\n", null, 1, true,
                [new Dictionary<string, object?> { ["customer_id"] = 1, ["total"] = 100 }]));

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        outcome.FinalSql.Should().Be(repairedSql);
        outcome.Repairs.Should().ContainSingle();
        outcome.Repairs[0].Trigger.Should().Be("lint");
        outcome.Repairs[0].Succeeded.Should().BeTrue();
        outcome.Repairs[0].RetriedSql.Should().Be(repairedSql);
        outcome.LintFindings.Should().BeEmpty("the repaired SQL groups by every non-aggregated column");
        outcome.Text.Should().NotContain("### Semantic warnings");
    }

    [Test]
    public async Task RunAsync_LintRepairDoesNotReduceFindings_KeepsOriginalSqlAndRendersWarnings()
    {
        // R12: when the repair attempt does not lint strictly cleaner, the ORIGINAL sql must proceed,
        // and its findings must still be surfaced to the caller as "### Semantic warnings".
        const string groupByMismatchSql = "SELECT o.customer_id, SUM(o.amount) AS total FROM public.orders o";
        const string worseRetrySql = "SELECT o.customer_id, o.region, SUM(o.amount) AS total FROM public.orders o";

        _sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), _settings, It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(groupByMismatchSql, ["orders"]));

        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), groupByMismatchSql,
                It.Is<string>(s => s.StartsWith("Semantic check:", StringComparison.Ordinal)),
                It.IsAny<string>(), It.IsAny<string?>(), Question, It.IsAny<CancellationToken>()))
            .ReturnsAsync(worseRetrySql);

        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, groupByMismatchSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (1 row)\n", null, 1, true,
                [new Dictionary<string, object?> { ["customer_id"] = 1, ["total"] = 100 }]));

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        outcome.FinalSql.Should().Be(groupByMismatchSql, "the retry lints no cleaner, so the original SQL must be kept");
        outcome.Repairs.Should().ContainSingle();
        outcome.Repairs[0].Trigger.Should().Be("lint");
        outcome.Repairs[0].Succeeded.Should().BeFalse();
        outcome.Repairs[0].RetriedSql.Should().BeNull();
        outcome.LintFindings.Should().ContainSingle(x => x.Code == "GROUP_BY_MISMATCH");
        outcome.Text.Should().Contain("### Semantic warnings");
        outcome.Text.Should().Contain("GROUP_BY_MISMATCH");

        // Never executed against — proves the rejected repair candidate was dropped entirely.
        _executor.Verify(x => x.ExecuteAsync(DataSourceId, worseRetrySql, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task RunAsync_SemanticLintDisabled_RunsNoLintAndRendersNoWarnings()
    {
        // R9: while EnableSemanticLint is false, no lint runs and no "### Semantic warnings" is rendered —
        // even for SQL that would otherwise flag GROUP_BY_MISMATCH.
        const string groupByMismatchSql = "SELECT o.customer_id, SUM(o.amount) AS total FROM public.orders o";
        _settings.EnableSemanticLint = false;

        _sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), _settings, It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(groupByMismatchSql, ["orders"]));

        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, groupByMismatchSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (1 row)\n", null, 1, true,
                [new Dictionary<string, object?> { ["customer_id"] = 1, ["total"] = 100 }]));

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        outcome.FinalSql.Should().Be(groupByMismatchSql);
        outcome.LintFindings.Should().BeEmpty();
        outcome.Repairs.Should().BeEmpty("no repair trigger should fire when semantic lint is disabled");
        outcome.Text.Should().NotContain("### Semantic warnings");

        _sqlGeneration.Verify(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            It.Is<string>(s => s.StartsWith("Semantic check:", StringComparison.Ordinal)),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // NF1: lint findings are computed once, right after schema validation — BEFORE a dry-run repair can
    // swap in a different SQL. The pipeline must re-lint the FINAL sql before it renders warnings, so a
    // dry-run repair that introduces a semantic issue is not silently missed.
    [Test]
    public async Task RunAsync_DryRunRepairChangesSql_SemanticWarningsReflectTheRepairedSql()
    {
        const string cleanSql = "SELECT o.customer_id, SUM(o.amount) AS total FROM public.orders o GROUP BY o.customer_id";
        const string dryRunRepairSql = "SELECT o.customer_id, SUM(o.amount) AS total FROM public.orders o";

        _sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), _settings, It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(cleanSql, ["orders"]));

        _executor
            .Setup(x => x.DryRunAsync(DataSourceId, cleanSql, It.IsAny<CancellationToken>()))
            .ReturnsAsync("planner error: bad estimate");
        _executor
            .Setup(x => x.DryRunAsync(DataSourceId, dryRunRepairSql, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), cleanSql,
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), Question, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dryRunRepairSql);

        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, dryRunRepairSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (1 row)\n", null, 1, true,
                [new Dictionary<string, object?> { ["customer_id"] = 1, ["total"] = 100 }]));

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        outcome.FinalSql.Should().Be(dryRunRepairSql);
        outcome.Repairs.Should().ContainSingle(x => x.Trigger == "dry-run" && x.Succeeded);
        outcome.LintFindings.Should().ContainSingle(x => x.Code == "GROUP_BY_MISMATCH",
            "the SQL that actually ran (after the dry-run repair) lacks GROUP BY, even though the original generation was lint-clean");
        outcome.Text.Should().Contain("### Semantic warnings");
        outcome.Text.Should().Contain("GROUP_BY_MISMATCH");
    }

    private AskSqlPipeline CreatePipeline()
    {
        return new AskSqlPipeline(
            _knowledgeGraph.Object,
            _sqlGeneration.Object,
            TestSqlGate.Create(_guardrail.Object),
            NullLogger<AskSqlPipeline>.Instance);
    }
}

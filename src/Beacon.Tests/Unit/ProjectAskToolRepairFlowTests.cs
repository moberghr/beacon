using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;

namespace Beacon.Tests.Unit;

/// <summary>
/// Repair-flow scenarios for the shared <see cref="AskSqlPipeline"/> (moved here, verbatim in behaviour,
/// from <c>ProjectAskTool.GenerateAndExecuteSqlAsync</c> — § Architecture ①). A mocked
/// <see cref="IAskSqlExecutor"/> replaces the MCP <c>IQueryExecutionService</c> the tool used to depend
/// on directly.
/// </summary>
[TestFixture]
public class ProjectAskToolRepairFlowTests
{
    private const int DataSourceId = 7;
    private const int ProjectId = 42;
    private const string Question = "How many orders last week?";
    private const string NonCountQuestion = "Show me the orders placed last week";
    private const string GeneratedSql = "SELECT count(*) FROM orders";
    private const string CorrectedSql = "SELECT count(*) FROM public.orders";

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

        _sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), _settings, It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(GeneratedSql, ["orders"]));

        _guardrail
            .Setup(x => x.ValidateQuery(It.IsAny<string>(), It.IsAny<QueryGuardrailOptions>()))
            .Returns(new QueryValidationResult(true));
    }

    [Test]
    public async Task DryRunFailure_TriggersRepairBeforeExecution()
    {
        _executor
            .Setup(x => x.DryRunAsync(DataSourceId, GeneratedSql, It.IsAny<CancellationToken>()))
            .ReturnsAsync("column \"bogus\" does not exist");
        _executor
            .Setup(x => x.DryRunAsync(DataSourceId, CorrectedSql, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), GeneratedSql,
                "column \"bogus\" does not exist", It.IsAny<string>(), null, Question, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CorrectedSql);
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, CorrectedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (3 rows)\n", null, 3, true));

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        outcome.Text.Should().Contain("failed dry-run validation");
        outcome.Text.Should().Contain(CorrectedSql);
        _executor.Verify(x => x.ExecuteAsync(DataSourceId, CorrectedSql, 100, It.IsAny<CancellationToken>()), Times.Once);
        _executor.Verify(x => x.ExecuteAsync(DataSourceId, GeneratedSql, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        outcome.DryRunError.Should().Contain("bogus");
        outcome.Repairs.Should().ContainSingle(x => x.Trigger == "dry-run" && x.Succeeded);
        outcome.CorrectedSql.Should().Be(CorrectedSql);
    }

    [Test]
    public async Task EmptyResult_TriggersExactlyOneRetry()
    {
        SetupCleanDryRun();
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("No results returned.\n", null, 0, true));
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), GeneratedSql,
                It.Is<string>(y => y.Contains("zero rows")), It.IsAny<string>(), null, NonCountQuestion, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CorrectedSql);
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, CorrectedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (5 rows)\n", null, 5, true));

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, NonCountQuestion, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        outcome.Text.Should().Contain("returned zero rows, retried");
        outcome.Text.Should().Contain("### Results (5 rows)");
        _sqlGeneration.Verify(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        outcome.EmptyResultRetried.Should().BeTrue();
    }

    [Test]
    public async Task EmptyResult_IdenticalRetrySql_AcceptsZeroRowsWithoutSecondExecution()
    {
        SetupCleanDryRun();
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("No results returned.\n", null, 0, true));
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), GeneratedSql,
                It.Is<string>(y => y.Contains("zero rows")), It.IsAny<string>(), null, NonCountQuestion, It.IsAny<CancellationToken>()))
            .ReturnsAsync(GeneratedSql + ";");

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, NonCountQuestion, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        outcome.Text.Should().Contain("No results returned.");
        _executor.Verify(x => x.ExecuteAsync(DataSourceId, It.IsAny<string>(), 100, It.IsAny<CancellationToken>()), Times.Once);
        outcome.EmptyResultRetried.Should().BeTrue();
    }

    [Test]
    public async Task EmptyResult_RetryAlsoEmpty_ReturnsOriginalZeroRowResult()
    {
        SetupCleanDryRun();
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("No results returned.\n", null, 0, true));
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), GeneratedSql,
                It.Is<string>(y => y.Contains("zero rows")), It.IsAny<string>(), null, NonCountQuestion, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CorrectedSql);
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, CorrectedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("No results returned.\n", null, 0, true));

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, NonCountQuestion, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        outcome.Text.Should().Contain("No results returned.");
        outcome.Text.Should().NotContain("### Results");
        outcome.Repairs.Should().ContainSingle(x => x.Trigger == "empty-result" && !x.Succeeded);
    }

    [Test]
    public async Task RepairBudget_CapsAtTwoLlmRepairCalls()
    {
        // Dry-run fails (repair 1, still failing), then execution fails (repair 2, still failing),
        // then the zero-row/empty branch must NOT fire a third repair.
        _executor
            .Setup(x => x.DryRunAsync(DataSourceId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("dry-run error");
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CorrectedSql);
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, It.IsAny<string>(), 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult(null, "execution error", 0, false));

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        _sqlGeneration.Verify(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        outcome.Text.Should().Contain("**Execution Error:** execution error");
        outcome.EmptyResultRetried.Should().BeFalse();
    }

    [Test]
    public async Task EmptyResult_CountStyleQuestion_AcceptsZeroRowsWithoutRetry()
    {
        SetupCleanDryRun();
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("No results returned.\n", null, 0, true));

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        outcome.Text.Should().Contain("No results returned.");
        _sqlGeneration.Verify(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        outcome.EmptyResultRetried.Should().BeFalse();
    }

    [TestCase("How many orders failed?", true)]
    [TestCase("Count the active users", true)]
    [TestCase("Are there any overdue invoices?", true)]
    [TestCase("Is there a customer named Acme?", true)]
    [TestCase("Show me the orders placed last week", false)]
    [TestCase("List customers from Berlin", false)]
    public void QuestionExpectsCountOrExistence_ClassifiesQuestions(string question, bool expected)
    {
        AskSqlPipeline.QuestionExpectsCountOrExistence(question).Should().Be(expected);
    }

    [Test]
    public async Task ExecuteFalse_SkipsDryRunAndExecution()
    {
        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object,
            new AskSqlPipelineOptions(Execute: false), CancellationToken.None);

        outcome.Text.Should().Contain(GeneratedSql);
        _executor.Verify(x => x.DryRunAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _executor.Verify(x => x.ExecuteAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task DryRunInfrastructureFailure_NeverBlocksExecution()
    {
        _executor
            .Setup(x => x.DryRunAsync(DataSourceId, GeneratedSql, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection refused"));
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (2 rows)\n", null, 2, true));

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        outcome.Text.Should().Contain("### Results (2 rows)");
        _sqlGeneration.Verify(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // F002: constructs the REAL ProjectAskTool (not the pipeline) with a mocked IAskSqlPipeline
    // returning a canned AskSqlOutcome carrying one AskRepairStep per trigger, and asserts every
    // McpSignalBuilder field GenerateAndExecuteSqlAsync is supposed to set from it — including the
    // "lint" trigger (SF-2), which has no dedicated McpQuerySignal field and falls through to SetRetry
    // (when a corrected SQL was adopted) plus an informational log.
    [Test]
    public async Task GenerateAndExecuteSqlAsync_MapsEveryRepairTriggerOntoTheSignal()
    {
        var canned = new AskSqlOutcome(
            GeneratedSql,
            CorrectedSql,
            ["orders"],
            true,
            "### Generated SQL\n```sql\n" + GeneratedSql + "\n```\n",
            new AskExecutionResult("### Results (1 rows)\n", null, 1, true),
            null,
            "schema mismatch",
            "dry-run boom",
            [
                new AskRepairStep("schema", "schema mismatch", null, false),
                new AskRepairStep("dry-run", "dry-run boom", null, false),
                new AskRepairStep("execution", "execution boom", null, false),
                new AskRepairStep("empty-result", "zero rows", null, false),
                new AskRepairStep("lint", "GROUP_BY_MISMATCH: cust missing", CorrectedSql, true)
            ],
            false,
            null,
            [],
            null,
            [],
            CorrectedSql,
            ["orders"]);

        var pipeline = new Mock<IAskSqlPipeline>();
        pipeline
            .Setup(x => x.RunAsync(
                It.IsAny<ILlmProvider>(), DataSourceId, ProjectId, Question, It.IsAny<McpSettingsData>(),
                It.IsAny<IAskSqlExecutor>(), It.IsAny<AskSqlPipelineOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(canned);

        var tool = new ProjectAskTool(
            Mock.Of<IKnowledgeGraphService>(),
            Mock.Of<IMcpSettingsProvider>(),
            Mock.Of<IServiceProvider>(),
            Mock.Of<IProjectContext>(),
            null!,
            null!,
            pipeline.Object,
            _executor.Object,
            Mock.Of<IIntentClassifier>(),
            Mock.Of<IDataSourceRouter>(),
            Mock.Of<IKnowledgeAnswerService>(),
            Mock.Of<ICrossSourceQueryService>(),
            NullLogger<ProjectAskTool>.Instance);

        var signal = new McpSignalBuilder().SetTool("ask").SetQuestion(Question);

        var (text, succeeded) = await tool.GenerateAndExecuteSqlAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, true, signal, CancellationToken.None);

        succeeded.Should().BeTrue();
        text.Should().Contain(GeneratedSql);

        var built = signal.Build();
        built.GeneratedSql.Should().Be(GeneratedSql);
        built.TablesUsed.Should().Contain("orders");
        built.SchemaValidationFailed.Should().BeTrue();
        built.SchemaValidationError.Should().Be("schema mismatch");
        built.DryRunFailed.Should().BeTrue();
        built.DryRunError.Should().Be("dry-run boom");
        built.ExecutionFailed.Should().BeTrue();
        built.ExecutionError.Should().Be("execution boom");
        built.EmptyResultRetryAttempted.Should().BeTrue();

        // The "lint" trigger has no dedicated signal field; its RetriedSql still reaches SetRetry.
        built.RetryAttempted.Should().BeTrue();
        built.RetrySucceeded.Should().BeTrue();
        built.CorrectedSql.Should().Be(CorrectedSql);
    }

    // Ported from main: drives the real ProjectAskTool.GenerateAndExecuteSqlAsync with a mocked
    // IAskSqlPipeline returning an outcome whose Execution.Structured carries the columns/rows payload,
    // and asserts the mapped AskToolOutcome (ResultPayload/GeneratedSql/CorrectedSql) — the SQL/signal
    // fields the ask tool's structured content is built from.
    [Test]
    public async Task StructuredContent_WithoutRepair_CarriesGeneratedSqlAndOmitsCorrectedSql()
    {
        var structuredPayload = new JsonObject
        {
            ["columns"] = new JsonArray("id"),
            ["rows"] = new JsonArray(new JsonArray(1)),
            ["row_count"] = 1,
            ["truncated"] = false
        };

        var canned = new AskSqlOutcome(
            GeneratedSql,
            GeneratedSql,
            ["orders"],
            true,
            "### Generated SQL\n```sql\n" + GeneratedSql + "\n```\n",
            new AskExecutionResult("### Results (1 rows)\n", null, 1, true, Structured: structuredPayload),
            null,
            null,
            null,
            [],
            false,
            null,
            [],
            null,
            [],
            null,
            []);

        var pipeline = new Mock<IAskSqlPipeline>();
        pipeline
            .Setup(x => x.RunAsync(
                It.IsAny<ILlmProvider>(), DataSourceId, ProjectId, Question, It.IsAny<McpSettingsData>(),
                It.IsAny<IAskSqlExecutor>(), It.IsAny<AskSqlPipelineOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(canned);

        var tool = new ProjectAskTool(
            Mock.Of<IKnowledgeGraphService>(),
            Mock.Of<IMcpSettingsProvider>(),
            Mock.Of<IServiceProvider>(),
            Mock.Of<IProjectContext>(),
            null!,
            null!,
            pipeline.Object,
            _executor.Object,
            Mock.Of<IIntentClassifier>(),
            Mock.Of<IDataSourceRouter>(),
            Mock.Of<IKnowledgeAnswerService>(),
            Mock.Of<ICrossSourceQueryService>(),
            NullLogger<ProjectAskTool>.Instance);

        var signal = new McpSignalBuilder().SetTool("ask").SetQuestion(Question);

        var outcome = await tool.GenerateAndExecuteSqlAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, true, signal, CancellationToken.None);

        outcome.GeneratedSql.Should().Be(GeneratedSql);
        outcome.CorrectedSql.Should().BeNull();
        outcome.ResultPayload.Should().BeSameAs(structuredPayload);

        var structured = ProjectAskTool.BuildAskStructuredContent(7, outcome.GeneratedSql, outcome.CorrectedSql, outcome.ResultPayload)!.AsObject();
        structured["signal_id"]!.GetValue<int>().Should().Be(7);
        structured["generated_sql"]!.GetValue<string>().Should().Be(GeneratedSql);
        structured.ContainsKey("corrected_sql").Should().BeFalse();
    }

    // TEST-7: the MCP-side IAskSqlExecutor adapter over IQueryExecutionService — Rows stays null and
    // FormattedResult/ErrorMessage/RowCount/IsSuccess pass through unchanged; DryRunAsync delegates to
    // ValidateAsync verbatim.
    [Test]
    public async Task AskSqlExecutor_ExecuteAsync_PassesThroughFieldsWithNullRows()
    {
        var queryExecutionService = new Mock<IQueryExecutionService>();
        queryExecutionService
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionResult("### Results (2 rows)\n", null, 2, true));

        var adapter = new AskSqlExecutor(queryExecutionService.Object);

        var result = await adapter.ExecuteAsync(DataSourceId, GeneratedSql, 100, CancellationToken.None);

        result.FormattedResult.Should().Be("### Results (2 rows)\n");
        result.ErrorMessage.Should().BeNull();
        result.RowCount.Should().Be(2);
        result.IsSuccess.Should().BeTrue();
        result.Rows.Should().BeNull();
    }

    [Test]
    public async Task AskSqlExecutor_DryRunAsync_DelegatesToValidateAsync()
    {
        var queryExecutionService = new Mock<IQueryExecutionService>();
        queryExecutionService
            .Setup(x => x.ValidateAsync(DataSourceId, GeneratedSql, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderDryRunOutcome("column \"bogus\" does not exist", false));

        var adapter = new AskSqlExecutor(queryExecutionService.Object);

        var error = await adapter.DryRunAsync(DataSourceId, GeneratedSql, CancellationToken.None);

        error.Should().Be("column \"bogus\" does not exist");
        queryExecutionService.Verify(x => x.ValidateAsync(DataSourceId, GeneratedSql, It.IsAny<CancellationToken>()), Times.Once);
    }

    // Ported from main (adapted to the AskSqlExecutor adapter, since the skip-never-triggers-repair
    // semantics now live on the executor seam, not on ProjectAskTool directly): a Skipped provider
    // dry-run outcome means NOTHING was checked, so DryRunAsync must surface null (never a repair
    // trigger, §1.5 codex PR-11 R4); ExecuteAsync still passes the Structured payload through.
    [Test]
    public async Task SkippedProviderDryRun_NeverTriggersRepair_AndNeverBlocksExecution()
    {
        var queryExecutionService = new Mock<IQueryExecutionService>();
        queryExecutionService
            .Setup(x => x.ValidateAsync(DataSourceId, GeneratedSql, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderDryRunOutcome("Provider dry-run validation is not supported for engine SQLite", true));
        var structuredPayload = new JsonObject { ["row_count"] = 2 };
        queryExecutionService
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionResult("### Results (2 rows)\n", null, 2, true, structuredPayload));

        var adapter = new AskSqlExecutor(queryExecutionService.Object);

        var dryRunError = await adapter.DryRunAsync(DataSourceId, GeneratedSql, CancellationToken.None);
        dryRunError.Should().BeNull("a skipped provider dry-run checked nothing, so it must never surface as a repair trigger");

        var execResult = await adapter.ExecuteAsync(DataSourceId, GeneratedSql, 100, CancellationToken.None);
        execResult.IsSuccess.Should().BeTrue();
        execResult.Structured.Should().BeSameAs(structuredPayload);
    }

    [Test]
    public void BuildAskStructuredContent_ResultPayload_MergesKeysAlongsideSqlAndSignal()
    {
        var payload = new JsonObject
        {
            ["columns"] = new JsonArray("id", "name"),
            ["rows"] = new JsonArray(new JsonArray(1, "acme")),
            ["row_count"] = 1,
            ["truncated"] = false
        };

        var structured = ProjectAskTool.BuildAskStructuredContent(42, GeneratedSql, null, payload)!.AsObject();

        structured["signal_id"]!.GetValue<int>().Should().Be(42);
        structured["generated_sql"]!.GetValue<string>().Should().Be(GeneratedSql);
        structured["columns"]!.AsArray().Select(x => x!.GetValue<string>()).Should().Equal("id", "name");
        structured["row_count"]!.GetValue<int>().Should().Be(1);
        structured["truncated"]!.GetValue<bool>().Should().BeFalse();
        var row = structured["rows"]!.AsArray().Single()!.AsArray();
        row[0]!.GetValue<int>().Should().Be(1);
        row[1]!.GetValue<string>().Should().Be("acme");
    }

    [Test]
    public void BuildAskStructuredContent_NullSignalId_OmitsSignalIdKey()
    {
        var structured = ProjectAskTool.BuildAskStructuredContent(null, GeneratedSql, null, null)!.AsObject();

        structured.ContainsKey("signal_id").Should().BeFalse();
        structured["generated_sql"]!.GetValue<string>().Should().Be(GeneratedSql);
    }

    [Test]
    public void BuildAskStructuredContent_AllNullArguments_ReturnsNull()
    {
        ProjectAskTool.BuildAskStructuredContent(null, null, null, null).Should().BeNull();
    }

    private void SetupCleanDryRun()
    {
        _executor
            .Setup(x => x.DryRunAsync(DataSourceId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
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

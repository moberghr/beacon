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
public class ProjectAskToolRepairFlowTests
{
    private const int DataSourceId = 7;
    private const string Question = "How many orders last week?";
    private const string NonCountQuestion = "Show me the orders placed last week";
    private const string GeneratedSql = "SELECT count(*) FROM orders";
    private const string CorrectedSql = "SELECT count(*) FROM public.orders";

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
        _settings = new McpSettingsData();

        _knowledgeGraph
            .Setup(x => x.GetSmartContextForAskAsync(DataSourceId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        _queryExecution
            .Setup(x => x.ValidateAsync(DataSourceId, GeneratedSql, It.IsAny<CancellationToken>()))
            .ReturnsAsync("column \"bogus\" does not exist");
        _queryExecution
            .Setup(x => x.ValidateAsync(DataSourceId, CorrectedSql, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), GeneratedSql,
                "column \"bogus\" does not exist", It.IsAny<string>(), null, Question, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CorrectedSql);
        _queryExecution
            .Setup(x => x.ExecuteAsync(DataSourceId, CorrectedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionResult("### Results (3 rows)\n", null, 3, true));

        var signal = new McpSignalBuilder();
        var (text, _) = await CreateTool().GenerateAndExecuteSqlAsync(
            _llmProvider.Object, DataSourceId, Question, _settings, execute: true, signal, CancellationToken.None);

        text.Should().Contain("failed dry-run validation");
        text.Should().Contain(CorrectedSql);
        _queryExecution.Verify(x => x.ExecuteAsync(DataSourceId, CorrectedSql, 100, It.IsAny<CancellationToken>()), Times.Once);
        _queryExecution.Verify(x => x.ExecuteAsync(DataSourceId, GeneratedSql, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        signal.Build().DryRunFailed.Should().BeTrue();
        signal.Build().DryRunError.Should().Contain("bogus");
    }

    [Test]
    public async Task EmptyResult_TriggersExactlyOneRetry()
    {
        SetupCleanDryRun();
        _queryExecution
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionResult("No results returned.\n", null, 0, true));
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), GeneratedSql,
                It.Is<string>(y => y.Contains("zero rows")), It.IsAny<string>(), null, NonCountQuestion, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CorrectedSql);
        _queryExecution
            .Setup(x => x.ExecuteAsync(DataSourceId, CorrectedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionResult("### Results (5 rows)\n", null, 5, true));

        var signal = new McpSignalBuilder();
        var (text, _) = await CreateTool().GenerateAndExecuteSqlAsync(
            _llmProvider.Object, DataSourceId, NonCountQuestion, _settings, execute: true, signal, CancellationToken.None);

        text.Should().Contain("returned zero rows, retried");
        text.Should().Contain("### Results (5 rows)");
        _sqlGeneration.Verify(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        signal.Build().EmptyResultRetryAttempted.Should().BeTrue();
    }

    [Test]
    public async Task EmptyResult_IdenticalRetrySql_AcceptsZeroRowsWithoutSecondExecution()
    {
        SetupCleanDryRun();
        _queryExecution
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionResult("No results returned.\n", null, 0, true));
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), GeneratedSql,
                It.Is<string>(y => y.Contains("zero rows")), It.IsAny<string>(), null, NonCountQuestion, It.IsAny<CancellationToken>()))
            .ReturnsAsync(GeneratedSql + ";");

        var signal = new McpSignalBuilder();
        var (text, _) = await CreateTool().GenerateAndExecuteSqlAsync(
            _llmProvider.Object, DataSourceId, NonCountQuestion, _settings, execute: true, signal, CancellationToken.None);

        text.Should().Contain("No results returned.");
        _queryExecution.Verify(x => x.ExecuteAsync(DataSourceId, It.IsAny<string>(), 100, It.IsAny<CancellationToken>()), Times.Once);
        signal.Build().EmptyResultRetryAttempted.Should().BeTrue();
    }

    [Test]
    public async Task EmptyResult_RetryAlsoEmpty_ReturnsOriginalZeroRowResult()
    {
        SetupCleanDryRun();
        _queryExecution
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionResult("No results returned.\n", null, 0, true));
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), GeneratedSql,
                It.Is<string>(y => y.Contains("zero rows")), It.IsAny<string>(), null, NonCountQuestion, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CorrectedSql);
        _queryExecution
            .Setup(x => x.ExecuteAsync(DataSourceId, CorrectedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionResult("No results returned.\n", null, 0, true));

        var signal = new McpSignalBuilder();
        var (text, _) = await CreateTool().GenerateAndExecuteSqlAsync(
            _llmProvider.Object, DataSourceId, NonCountQuestion, _settings, execute: true, signal, CancellationToken.None);

        text.Should().Contain("No results returned.");
        text.Should().NotContain("### Results");
        signal.Build().RetrySucceeded.Should().BeFalse();
    }

    [Test]
    public async Task RepairBudget_CapsAtTwoLlmRepairCalls()
    {
        // Dry-run fails (repair 1, still failing), then execution fails (repair 2, still failing),
        // then the zero-row/empty branch must NOT fire a third repair.
        _queryExecution
            .Setup(x => x.ValidateAsync(DataSourceId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("dry-run error");
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CorrectedSql);
        _queryExecution
            .Setup(x => x.ExecuteAsync(DataSourceId, It.IsAny<string>(), 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionResult(null, "execution error", 0, false));

        var signal = new McpSignalBuilder();
        var (text, _) = await CreateTool().GenerateAndExecuteSqlAsync(
            _llmProvider.Object, DataSourceId, Question, _settings, execute: true, signal, CancellationToken.None);

        _sqlGeneration.Verify(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        text.Should().Contain("**Execution Error:** execution error");
        signal.Build().EmptyResultRetryAttempted.Should().BeFalse();
    }

    [Test]
    public async Task EmptyResult_CountStyleQuestion_AcceptsZeroRowsWithoutRetry()
    {
        SetupCleanDryRun();
        _queryExecution
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionResult("No results returned.\n", null, 0, true));

        var signal = new McpSignalBuilder();
        var (text, _) = await CreateTool().GenerateAndExecuteSqlAsync(
            _llmProvider.Object, DataSourceId, Question, _settings, execute: true, signal, CancellationToken.None);

        text.Should().Contain("No results returned.");
        _sqlGeneration.Verify(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        signal.Build().EmptyResultRetryAttempted.Should().BeFalse();
    }

    [TestCase("How many orders failed?", true)]
    [TestCase("Count the active users", true)]
    [TestCase("Are there any overdue invoices?", true)]
    [TestCase("Is there a customer named Acme?", true)]
    [TestCase("Show me the orders placed last week", false)]
    [TestCase("List customers from Berlin", false)]
    public void QuestionExpectsCountOrExistence_ClassifiesQuestions(string question, bool expected)
    {
        ProjectAskTool.QuestionExpectsCountOrExistence(question).Should().Be(expected);
    }

    [Test]
    public async Task ExecuteFalse_SkipsDryRunAndExecution()
    {
        var signal = new McpSignalBuilder();
        var (text, _) = await CreateTool().GenerateAndExecuteSqlAsync(
            _llmProvider.Object, DataSourceId, Question, _settings, execute: false, signal, CancellationToken.None);

        text.Should().Contain(GeneratedSql);
        _queryExecution.Verify(x => x.ValidateAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _queryExecution.Verify(x => x.ExecuteAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task DryRunInfrastructureFailure_NeverBlocksExecution()
    {
        _queryExecution
            .Setup(x => x.ValidateAsync(DataSourceId, GeneratedSql, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection refused"));
        _queryExecution
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionResult("### Results (2 rows)\n", null, 2, true));

        var signal = new McpSignalBuilder();
        var (text, _) = await CreateTool().GenerateAndExecuteSqlAsync(
            _llmProvider.Object, DataSourceId, Question, _settings, execute: true, signal, CancellationToken.None);

        text.Should().Contain("### Results (2 rows)");
        _sqlGeneration.Verify(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private void SetupCleanDryRun()
    {
        _queryExecution
            .Setup(x => x.ValidateAsync(DataSourceId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
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

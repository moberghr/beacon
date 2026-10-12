using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Configuration;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;

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
            .Setup(x => x.GetSmartContextForAskAsync(DataSourceId, ProjectId, It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
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
            .ReturnsAsync("relation \"orders\" does not exist");
        _executor
            .Setup(x => x.DryRunAsync(DataSourceId, CorrectedSql, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), GeneratedSql,
                "relation \"orders\" does not exist", It.IsAny<string>(), null, Question, It.IsAny<CancellationToken>()))
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
        outcome.DryRunError.Should().Contain("orders");
        outcome.Repairs.Should().ContainSingle(x => x.Trigger == "dry-run" && x.Succeeded);
        outcome.CorrectedSql.Should().Be(CorrectedSql);
    }

    // A database error that quotes a row value goes into the repair prompt as its class, code and a generic description
    // only — and the log carries none of the value either.
    [Test]
    public async Task AnExecutionErrorQuotingARowValue_ReachesTheRepairPromptRedacted()
    {
        SetupCleanDryRun();
        var log = new LogRecorder();
        string? prompted = null;
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult(null, "22P02: invalid input syntax for type integer: \"Ivan Horvat\" in column \"orders\"; DETAIL: Key (id)=(ivan@bank.example)", 0, false));
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), GeneratedSql,
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), Question, It.IsAny<CancellationToken>()))
            .Callback<ILlmProvider, string, string, string, string, string?, string, CancellationToken>((_, _, _, error, _, _, _, _) => prompted = error)
            .ReturnsAsync((string?)null);
        var pipeline = new AskSqlPipeline(
            _knowledgeGraph.Object,
            _sqlGeneration.Object,
            TestSqlGate.Create(_guardrail.Object),
            TestRowDataEgress.Discarding(),
            log.For<AskSqlPipeline>());

        await pipeline.RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        prompted.Should().Be("Database error 22P02 (data exception): a value could not be converted to integer. The database's message is withheld because it can quote row values.");
        log.Contains("Horvat").Should().BeFalse();
        log.Contains("ivan@bank.example").Should().BeFalse();
    }

    // The caller reads a database error redacted while PII detection is on, as it was otherwise; the repair steps the
    // stored signal is built from hold it redacted either way.
    [TestCase(true)]
    [TestCase(false)]
    public async Task AnExecutionErrorQuotingARowValue_ReachesTheCallerByTheSetting_AndTheRepairStepsRedacted(bool piiDetectionOn)
    {
        SetupCleanDryRun();
        _settings.EnablePiiDetection = piiDetectionOn;
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult(null, ErrorQuotingAValue, 0, false));
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), GeneratedSql,
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), Question, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        outcome.Repairs.Should().ContainSingle(x => x.Trigger == "execution").Which.Error.Should().Be(RedactedError);
        if (piiDetectionOn)
        {
            outcome.Text.Should().Contain($"**Execution Error:** {RedactedError}").And.NotContain("Horvat");
            return;
        }

        outcome.Text.Should().Contain($"**Execution Error:** {ErrorQuotingAValue}");
    }

    [Test]
    public async Task ADryRunErrorQuotingARowValue_IsKeptRedactedOnTheOutcome()
    {
        _executor
            .Setup(x => x.DryRunAsync(DataSourceId, GeneratedSql, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ErrorQuotingAValue);
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), GeneratedSql,
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), Question, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (1 rows)\n", null, 1, true));

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        outcome.DryRunError.Should().Be(RedactedError);
        outcome.Repairs.Should().ContainSingle(x => x.Trigger == "dry-run").Which.Error.Should().Be(RedactedError);
    }

    // The repair prompt gets the dry-run error without the value it quotes, and the log its class only.
    [Test]
    public async Task ADryRunErrorQuotingARowValue_ReachesTheRepairPromptAndTheLogRedacted()
    {
        _executor
            .Setup(x => x.DryRunAsync(DataSourceId, GeneratedSql, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ErrorQuotingAValue);
        string? repairError = null;
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), GeneratedSql,
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), Question, It.IsAny<CancellationToken>()))
            .Callback<ILlmProvider, string, string, string, string, string?, string, CancellationToken>((_, _, _, error, _, _, _, _) => repairError = error)
            .ReturnsAsync((string?)null);
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (1 rows)\n", null, 1, true));
        var log = new LogRecorder();

        await CreatePipeline(log.For<AskSqlPipeline>()).RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        repairError.Should().Be(RedactedError);
        log.Entries.Should().Contain(x => x.Message == "Dry-run validation failed, retrying (Database error 22P02 (data exception).)");
        log.Contains("Horvat").Should().BeFalse();
    }

    private const string ErrorQuotingAValue = "22P02: invalid input syntax for type integer: \"Ivan Horvat\"";

    private const string RedactedError = "Database error 22P02 (data exception): a value could not be converted to integer. The database's message is withheld because it can quote row values.";

    // Every request whose prompt carries the context's row data is logged before it is sent, with ids and categories only.
    [Test]
    public async Task EveryPromptCarryingRowData_IsLoggedWithIdsAndCategoriesOnly()
    {
        SetupCleanDryRun();
        _knowledgeGraph
            .Setup(x => x.GetSmartContextForAskAsync(DataSourceId, ProjectId, It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SmartSchemaContext
            {
                FullContext = "schema-context Examples: [Horvat]",
                DatabaseDialect = "PostgreSQL",
                TotalTableCount = 1,
                RowDataCategories = [LlmRowDataCategories.SampleValues, LlmRowDataCategories.ValueMatches]
            });
        var llm = new Mock<ILlmProvider>();
        llm
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Beacon.Core.Models.Ai.LlmResponse { Content = GeneratedSql });
        var generation = new SqlGenerationService();
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, It.IsAny<string>(), 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (1 rows)\n", null, 1, true));
        var log = new LogRecorder();
        var pipeline = new AskSqlPipeline(
            _knowledgeGraph.Object,
            generation,
            TestSqlGate.Create(_guardrail.Object),
            TestRowDataEgress.Logging(log.For<LlmRowDataEgress>()),
            NullLogger<AskSqlPipeline>.Instance);

        await pipeline.RunAsync(llm.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        var calls = llm.Invocations.Count(x => x.Method.Name == nameof(ILlmProvider.CompleteAsync));
        calls.Should().BePositive("the request still goes through the provider handed in");
        var egress = log.Entries
            .Where(x => x.EventId == Beacon.Core.Authorization.ProjectAccess.ProjectAccessEvents.RowDataSentToLlm)
            .ToList();
        egress.Should().HaveCount(calls, "one record per request");
        egress.Should().OnlyContain(x => x.Message == $"An LLM request carrying sample-values, value-matches read from data sources {DataSourceId} of project {ProjectId} was handed to the provider for caller {TestRowDataEgress.CallerId}");
        log.Contains("Horvat").Should().BeFalse("a value never reaches the log");
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
    public async Task ExecuteAsync_ResolvesEffectiveSettingsForTheAuthorizedProject_NotTheGlobalRow()
    {
        // NF-1: the ask tool resolves settings for the AUTHORIZED project. Global says MaxRowLimit 1000, project 42
        // says 10; the knowledge branch must receive the project's effective settings, and no other project id
        // may be asked for.
        var settingsProvider = SettingsProviderMock.Create(
            new McpSettingsData { MaxRowLimit = 1000 },
            projectSettings: new Dictionary<int, McpSettingsData> { [ProjectId] = new McpSettingsData { MaxRowLimit = 10 } });

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(x => x.GetService(typeof(ILlmProvider))).Returns(_llmProvider.Object);

        var intentClassifier = new Mock<IIntentClassifier>();
        intentClassifier
            .Setup(x => x.ClassifyAsync(It.IsAny<ILlmProvider>(), Question, It.IsAny<CancellationToken>()))
            .ReturnsAsync(IntentClassification.Knowledge);

        McpSettingsData? handed = null;
        var knowledgeAnswer = new Mock<IKnowledgeAnswerService>();
        knowledgeAnswer
            .Setup(x => x.AnswerAsync(It.IsAny<ILlmProvider>(), ProjectId, Question, It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>()))
            .Callback<ILlmProvider, int, string, McpSettingsData, CancellationToken>((_, _, _, settings, _) => handed = settings)
            .ReturnsAsync("answer");

        // Audit + signal services are best-effort (§1.7): a bare factory keeps their paths runnable without a DB.
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        var tool = new ProjectAskTool(
            Mock.Of<IKnowledgeGraphService>(),
            settingsProvider.Object,
            SettingsProviderMock.Unshared(settingsProvider.Object),
            serviceProvider.Object,
            new McpProjectContext { UserId = 1, AllowedProjectIds = [ProjectId], QueryProjectIds = [ProjectId] },
            new McpAuditService(factory.Object, SettingsProviderMock.Create().Object, new HttpContextAccessor(), Options.Create(new McpDeploymentOptions()), NullLogger<McpAuditService>.Instance, new McpAuditOutcome(), Options.Create(new BeaconTelemetryOptions()), NullLoggerFactory.Instance),
            new McpSignalService(factory.Object, settingsProvider.Object, NullLogger<McpSignalService>.Instance),
            Mock.Of<IAskSqlPipeline>(),
            _executor.Object,
            intentClassifier.Object,
            Mock.Of<IDataSourceRouter>(),
            knowledgeAnswer.Object,
            Mock.Of<ICrossSourceQueryService>(),
            Options.Create(new McpDeploymentOptions()),
            NullLogger<ProjectAskTool>.Instance);

        var result = await tool.ExecuteAsync(Question, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        handed.Should().NotBeNull();
        handed!.MaxRowLimit.Should().Be(10, "project 42's override, not the global 1000");
        settingsProvider.Verify(x => x.GetEffectiveSettingsAsync(ProjectId, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        settingsProvider.Verify(x => x.GetEffectiveSettingsAsync(It.Is<int>(id => id != ProjectId), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ask runs its SQL under the strictest guardrails of the live projects that link the routed data sources.
    [Test]
    public async Task ExecuteAsync_ARoutedSourceAnotherProjectLinks_RunsUnderTheStricterGuardrails()
    {
        var settingsProvider = SettingsProviderMock.Create(
            projectSettings: new Dictionary<int, McpSettingsData> { [ProjectId] = new() { MaxRowLimit = 1000 } });
        var sharedSources = TestSharedSources.Linking(DataSourceId, new Dictionary<int, McpSettingsData>
        {
            [ProjectId] = new() { MaxRowLimit = 1000 },
            [ProjectId + 1] = new() { MaxRowLimit = 5, EnforceReadOnly = true }
        });
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(x => x.GetService(typeof(ILlmProvider))).Returns(_llmProvider.Object);
        var intentClassifier = new Mock<IIntentClassifier>();
        intentClassifier
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
        McpSettingsData? handed = null;
        var pipeline = new Mock<IAskSqlPipeline>();
        pipeline
            .Setup(x => x.RunAsync(It.IsAny<ILlmProvider>(), DataSourceId, ProjectId, Question, It.IsAny<McpSettingsData>(), It.IsAny<IAskSqlExecutor>(), It.IsAny<AskSqlPipelineOptions>(), It.IsAny<CancellationToken>()))
            .Callback<ILlmProvider, int, int, string, McpSettingsData, IAskSqlExecutor, AskSqlPipelineOptions, CancellationToken>((_, _, _, _, settings, _, _, _) => handed = settings)
            .ThrowsAsync(new InvalidOperationException("stopped after the settings were handed over"));
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        var tool = new ProjectAskTool(
            knowledgeGraph.Object,
            settingsProvider.Object,
            sharedSources,
            serviceProvider.Object,
            new McpProjectContext { UserId = 1, AllowedProjectIds = [ProjectId], QueryProjectIds = [ProjectId] },
            new McpAuditService(factory.Object, SettingsProviderMock.Create().Object, new HttpContextAccessor(), Options.Create(new McpDeploymentOptions()), NullLogger<McpAuditService>.Instance, new McpAuditOutcome(), Options.Create(new BeaconTelemetryOptions()), NullLoggerFactory.Instance),
            new McpSignalService(factory.Object, settingsProvider.Object, NullLogger<McpSignalService>.Instance),
            pipeline.Object,
            _executor.Object,
            intentClassifier.Object,
            router.Object,
            Mock.Of<IKnowledgeAnswerService>(),
            Mock.Of<ICrossSourceQueryService>(),
            Options.Create(new McpDeploymentOptions()),
            NullLogger<ProjectAskTool>.Instance);

        await tool.ExecuteAsync(Question, cancellationToken: CancellationToken.None);

        handed.Should().NotBeNull();
        handed!.MaxRowLimit.Should().Be(5);
        handed.EnforceReadOnly.Should().BeTrue();
    }

    [Test]
    public async Task ExecuteAsync_QuestionOverDefaultCap_FailsAuditedAndSignalled_BeforeTheLlm()
    {
        var auditLogs = new List<McpAuditLog>();
        var signals = new List<McpQuerySignal>();
        var serviceProvider = new Mock<IServiceProvider>();
        var intentClassifier = new Mock<IIntentClassifier>();
        var tool = CreateAuditedTool(auditLogs, signals, serviceProvider, intentClassifier);

        var result = await tool.ExecuteAsync(new string('q', 4_001), cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeTrue();
        var error = result.Content.OfType<TextContentBlock>().Single().Text;
        error.Should().Contain("4001").And.Contain("4000");

        // §1.7 / §9.5 — the early return is audited and signalled like every other failure, attributed to the
        // authorized project so that project's content-retention decision applies to the stored question.
        var audit = auditLogs.Should().ContainSingle().Subject;
        audit.Tool.Should().Be("ask");
        audit.ProjectId.Should().Be(ProjectId);
        audit.ErrorMessage.Should().Be(error);
        var signal = signals.Should().ContainSingle().Subject;
        signal.ExecutionFailed.Should().BeTrue();
        signal.ProjectId.Should().Be(ProjectId);

        serviceProvider.Verify(x => x.GetService(typeof(ILlmProvider)), Times.Never);
        intentClassifier.Verify(
            x => x.ClassifyAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task ARefusedAsk_RecordsTheCallersBeaconUser_OnItsSignalAndAuditRow()
    {
        var auditLogs = new List<McpAuditLog>();
        var signals = new List<McpQuerySignal>();
        var tool = CreateAuditedTool(auditLogs, signals, new Mock<IServiceProvider>(), new Mock<IIntentClassifier>(), userId: 17);

        var result = await tool.ExecuteAsync(new string('q', 4_001), cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeTrue();
        auditLogs.Should().ContainSingle().Which.UserId.Should().Be(17);
        signals.Should().ContainSingle().Which.UserId.Should().Be(17);
    }

    [Test]
    public async Task AnAnsweredAsk_RecordsTheCallersBeaconUser_OnItsSignalAndAuditRow()
    {
        var auditLogs = new List<McpAuditLog>();
        var signals = new List<McpQuerySignal>();
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(x => x.GetService(typeof(ILlmProvider))).Returns(_llmProvider.Object);
        var intentClassifier = new Mock<IIntentClassifier>();
        intentClassifier
            .Setup(x => x.ClassifyAsync(It.IsAny<ILlmProvider>(), Question, It.IsAny<CancellationToken>()))
            .ReturnsAsync(IntentClassification.Knowledge);
        var knowledgeAnswer = new Mock<IKnowledgeAnswerService>();
        knowledgeAnswer
            .Setup(x => x.AnswerAsync(It.IsAny<ILlmProvider>(), ProjectId, Question, It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Notifications go out after each run.");
        var tool = CreateAuditedTool(auditLogs, signals, serviceProvider, intentClassifier, userId: 17, knowledgeAnswer: knowledgeAnswer.Object);

        var result = await tool.ExecuteAsync(Question, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        auditLogs.Should().ContainSingle().Which.Should().Match<McpAuditLog>(x => x.UserId == 17 && x.ErrorMessage == null);
        signals.Should().ContainSingle().Which.UserId.Should().Be(17);
    }

    [Test]
    public async Task ExecuteAsync_QuestionAtDefaultCap_PassesTheLengthCheck()
    {
        var serviceProvider = new Mock<IServiceProvider>();
        var tool = CreateAuditedTool([], [], serviceProvider, new Mock<IIntentClassifier>());

        // No ILlmProvider is registered, so the call fails one step later — proving the length check let it through.
        var result = await tool.ExecuteAsync(new string('q', 4_000), cancellationToken: CancellationToken.None);

        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("AI features not configured");
        serviceProvider.Verify(x => x.GetService(typeof(ILlmProvider)), Times.Once);
    }

    [Test]
    public async Task ExecuteAsync_ConfiguredMaxQuestionChars_IsHonoured()
    {
        var options = new McpDeploymentOptions { Ceilings = new McpCeilingOptions { MaxQuestionChars = 10 } };
        var serviceProvider = new Mock<IServiceProvider>();
        var tool = CreateAuditedTool([], [], serviceProvider, new Mock<IIntentClassifier>(), options);

        var rejected = await tool.ExecuteAsync(new string('q', 11), cancellationToken: CancellationToken.None);
        var accepted = await tool.ExecuteAsync(new string('q', 10), cancellationToken: CancellationToken.None);

        (rejected.IsError ?? false).Should().BeTrue();
        rejected.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("11").And.Contain("10");
        accepted.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("AI features not configured");
        serviceProvider.Verify(x => x.GetService(typeof(ILlmProvider)), Times.Once, "only the question within the configured cap reaches the LLM step");
    }

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
            SettingsProviderMock.Create().Object,
            SettingsProviderMock.Unshared(SettingsProviderMock.Create().Object),
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
            Options.Create(new McpDeploymentOptions()),
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
            SettingsProviderMock.Create().Object,
            SettingsProviderMock.Unshared(SettingsProviderMock.Create().Object),
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
            Options.Create(new McpDeploymentOptions()),
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

    private AskSqlPipeline CreatePipeline(ILogger<AskSqlPipeline>? logger = null)
    {
        return new AskSqlPipeline(
            _knowledgeGraph.Object,
            _sqlGeneration.Object,
            TestSqlGate.Create(_guardrail.Object),
            TestRowDataEgress.Discarding(),
            logger ?? NullLogger<AskSqlPipeline>.Instance);
    }

    // The real audit + signal services over a capturing context (§4.7 — no DB), so a test can assert the rows
    // an early return writes.
    private ProjectAskTool CreateAuditedTool(
        List<McpAuditLog> auditLogs,
        List<McpQuerySignal> signals,
        Mock<IServiceProvider> serviceProvider,
        Mock<IIntentClassifier> intentClassifier,
        McpDeploymentOptions? deploymentOptions = null,
        int? userId = 1,
        IKnowledgeAnswerService? knowledgeAnswer = null)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AuditCaptureContext(auditLogs, signals));
        var settingsProvider = SettingsProviderMock.Create();

        return new ProjectAskTool(
            Mock.Of<IKnowledgeGraphService>(),
            settingsProvider.Object,
            SettingsProviderMock.Unshared(settingsProvider.Object),
            serviceProvider.Object,
            new McpProjectContext { UserId = userId, AllowedProjectIds = [ProjectId], QueryProjectIds = [ProjectId] },
            new McpAuditService(factory.Object, settingsProvider.Object, new HttpContextAccessor(), Options.Create(new McpDeploymentOptions()), NullLogger<McpAuditService>.Instance, new McpAuditOutcome(), Options.Create(new BeaconTelemetryOptions()), NullLoggerFactory.Instance),
            new McpSignalService(factory.Object, settingsProvider.Object, NullLogger<McpSignalService>.Instance),
            Mock.Of<IAskSqlPipeline>(),
            _executor.Object,
            intentClassifier.Object,
            Mock.Of<IDataSourceRouter>(),
            knowledgeAnswer ?? Mock.Of<IKnowledgeAnswerService>(),
            Mock.Of<ICrossSourceQueryService>(),
            Options.Create(deploymentOptions ?? new McpDeploymentOptions()),
            NullLogger<ProjectAskTool>.Instance);
    }

    private sealed class AuditCaptureContext : BeaconContext
    {
        private static readonly DbContextOptions<AuditCaptureContext> Options =
            new DbContextOptionsBuilder<AuditCaptureContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly Mock<DbSet<McpAuditLog>> _auditSet = new();
        private readonly Mock<DbSet<McpQuerySignal>> _signalSet = new();

        public AuditCaptureContext(List<McpAuditLog> auditLogs, List<McpQuerySignal> signals) : base(Options, "beacon")
        {
            _auditSet
                .Setup(x => x.Add(It.IsAny<McpAuditLog>()))
                .Callback<McpAuditLog>(auditLogs.Add);
            _signalSet
                .Setup(x => x.Add(It.IsAny<McpQuerySignal>()))
                .Callback<McpQuerySignal>(signals.Add);
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpAuditLog))
            {
                return (DbSet<TEntity>)(object)_auditSet.Object;
            }

            if (typeof(TEntity) == typeof(McpQuerySignal))
            {
                return (DbSet<TEntity>)(object)_signalSet.Object;
            }

            return base.Set<TEntity>();
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}

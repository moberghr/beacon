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
using Beacon.Core.Mcp;

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
            TestRowSecurity.Resolver,
            TestRowDataEgress.Discarding(),
            Options.Create(new McpDeploymentOptions()),
            new McpStatementLog(),
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

    private const string NameError = "42P01: relation \"orders\" does not exist";

    private const string RaisedError = "Msg 50000, Level 16, State 1: Customer ivan.horvat@bank.example is blocked";

    private const string WithheldRaisedError = "Database error 50000 (unrecognised): not an error Beacon recognises. The database's message is withheld because it can quote row values.";

    // A repair prompt gets the database's error redacted — a name error keeps the names it knows — or, while row data may not
    // reach the provider, its class and code only; after a dry run and after execution alike. The repair steps the stored
    // signal is built from keep the redacted error either way.
    [TestCase(false, NameError, RedactedError)]
    [TestCase(true, "Database error 42P01 (undefined table).", "Database error 22P02 (data exception).")]
    public async Task TheRepairPrompts_GetTheRedactedError_OrUnderTheRowDataLockItsClassOnly(bool forbidRowData, string dryRunPrompt, string executionPrompt)
    {
        var prompts = new List<string>();
        _executor
            .Setup(x => x.DryRunAsync(DataSourceId, GeneratedSql, It.IsAny<CancellationToken>()))
            .ReturnsAsync(NameError);
        _executor
            .Setup(x => x.DryRunAsync(DataSourceId, CorrectedSql, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, CorrectedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult(null, ErrorQuotingAValue, 0, false));
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), GeneratedSql,
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), Question, It.IsAny<CancellationToken>()))
            .Callback<ILlmProvider, string, string, string, string, string?, string, CancellationToken>((_, _, _, error, _, _, _, _) => prompts.Add(error))
            .ReturnsAsync(CorrectedSql);
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), CorrectedSql,
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), Question, It.IsAny<CancellationToken>()))
            .Callback<ILlmProvider, string, string, string, string, string?, string, CancellationToken>((_, _, _, error, _, _, _, _) => prompts.Add(error))
            .ReturnsAsync((string?)null);

        var outcome = await CreatePipeline(forbidRowData: forbidRowData).RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        prompts.Should().Equal(dryRunPrompt, executionPrompt);
        outcome.Repairs.Select(x => x.Error).Should().Equal(NameError, RedactedError);
    }

    // An error the statement's own code raised holds its author's values unquoted: the caller (detection on), the repair
    // prompt and the repair steps the stored signal is built from all get its class and code.
    [Test]
    public async Task ARaisedExecutionError_IsWithheldFromTheCallerTheRepairPromptAndTheRepairSteps()
    {
        SetupCleanDryRun();
        _settings.EnablePiiDetection = true;
        string? prompted = null;
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult(null, RaisedError, 0, false));
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), GeneratedSql,
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), Question, It.IsAny<CancellationToken>()))
            .Callback<ILlmProvider, string, string, string, string, string?, string, CancellationToken>((_, _, _, error, _, _, _, _) => prompted = error)
            .ReturnsAsync((string?)null);

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        prompted.Should().Be(WithheldRaisedError);
        outcome.Repairs.Should().ContainSingle(x => x.Trigger == "execution").Which.Error.Should().Be(WithheldRaisedError);
        outcome.Text.Should().Contain($"**Execution Error:** {WithheldRaisedError}").And.NotContain("ivan.horvat@bank.example");
    }

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
            TestRowSecurity.Resolver,
            TestRowDataEgress.Logging(log.For<LlmRowDataEgress>()),
            Options.Create(new McpDeploymentOptions()),
            new McpStatementLog(),
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
            .ReturnsAsync(new AskExecutionResult(null, "42601: syntax error at end of input", 0, false));

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        _sqlGeneration.Verify(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        outcome.Text.Should().Contain("**Execution Error:** 42601: syntax error at end of input");
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
            new McpAuditService(factory.Object, SettingsProviderMock.Create().Object, new HttpContextAccessor(), Options.Create(new McpDeploymentOptions()), NullLogger<McpAuditService>.Instance, new McpAuditOutcome(), Options.Create(new BeaconTelemetryOptions()), NullLoggerFactory.Instance, TestMcpAudit.ContentHasher, new McpStatementLog()),
            new McpSignalService(factory.Object, settingsProvider.Object, NullLogger<McpSignalService>.Instance),
            Mock.Of<IAskSqlPipeline>(),
            _executor.Object,
            intentClassifier.Object,
            Mock.Of<IDataSourceRouter>(),
            knowledgeAnswer.Object,
            Mock.Of<ICrossSourceQueryService>(),
            Options.Create(new McpDeploymentOptions()),
            new McpQueryConcurrencyLimiter(),
            TimeProvider.System,
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
            new McpAuditService(factory.Object, SettingsProviderMock.Create().Object, new HttpContextAccessor(), Options.Create(new McpDeploymentOptions()), NullLogger<McpAuditService>.Instance, new McpAuditOutcome(), Options.Create(new BeaconTelemetryOptions()), NullLoggerFactory.Instance, TestMcpAudit.ContentHasher, new McpStatementLog()),
            new McpSignalService(factory.Object, settingsProvider.Object, NullLogger<McpSignalService>.Instance),
            pipeline.Object,
            _executor.Object,
            intentClassifier.Object,
            router.Object,
            Mock.Of<IKnowledgeAnswerService>(),
            Mock.Of<ICrossSourceQueryService>(),
            Options.Create(new McpDeploymentOptions()),
            new McpQueryConcurrencyLimiter(),
            TimeProvider.System,
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
            new McpQueryConcurrencyLimiter(),
            TimeProvider.System,
            NullLogger<ProjectAskTool>.Instance);

        var signal = new McpSignalBuilder().SetTool("ask").SetQuestion(Question);

        var (text, succeeded) = await tool.GenerateAndExecuteSqlAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, true, signal, null, CancellationToken.None);

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
            new McpQueryConcurrencyLimiter(),
            TimeProvider.System,
            NullLogger<ProjectAskTool>.Instance);

        var signal = new McpSignalBuilder().SetTool("ask").SetQuestion(Question);

        var outcome = await tool.GenerateAndExecuteSqlAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, true, signal, null, CancellationToken.None);

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

    [Test]
    public async Task LlmBudget_SelfConsistency_RequestsOnlyTheCandidatesTheBudgetAllows_KeepingTheRepairsCovered()
    {
        _settings.EnableSelfConsistency = true;
        _settings.SelfConsistencyCandidateCount = 8;
        _settings.SelfConsistencyMinTables = 1;
        _executor
            .SetupSequence(x => x.DryRunAsync(DataSourceId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("dry-run error")
            .ReturnsAsync((string?)null);
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, It.IsAny<string>(), 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (1 rows)\n", null, 1, true));
        _sqlGeneration
            .Setup(x => x.GenerateCandidatesAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), _settings,
                It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ILlmProvider _, string _, string _, McpSettingsData _, int count, decimal _, CancellationToken _) =>
                Enumerable.Range(0, count)
                    .Select(_ => new SqlGenerationResult(GeneratedSql, ["orders"]))
                    .ToList());
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CorrectedSql);
        var budget = new AskLlmBudget(6);

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object,
            new AskSqlPipelineOptions { LlmBudget = budget }, CancellationToken.None);

        // The single candidate leaves 5 calls; 2 stay for the repair attempts, so 3 extra candidates instead of the 7 wanted.
        _sqlGeneration.Verify(x => x.GenerateCandidatesAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), _settings,
            3, It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Once);
        outcome.Repairs.Should().ContainSingle(x => x.Trigger == "dry-run", "a repair still had a call");
        budget.Used.Should().Be(5);
    }

    [Test]
    public async Task LlmBudget_SelfConsistency_WithOnlyTheRepairCallsLeft_SkipsTheVote()
    {
        SetupCleanDryRun();
        _settings.EnableSelfConsistency = true;
        _settings.SelfConsistencyMinTables = 1;
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, It.IsAny<string>(), 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (1 rows)\n", null, 1, true));
        var budget = new AskLlmBudget(3);

        await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object,
            new AskSqlPipelineOptions { LlmBudget = budget }, CancellationToken.None);

        _sqlGeneration.Verify(x => x.GenerateCandidatesAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<McpSettingsData>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);
        budget.Used.Should().Be(1);
    }

    [Test]
    public async Task LlmBudget_SpentByGeneration_SkipsTheRepairAndReturnsTheUnrepairedAttempt()
    {
        SetupCleanDryRun();
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult(null, "42601: syntax error at end of input", 0, false));
        var budget = new AskLlmBudget(1);

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object,
            new AskSqlPipelineOptions { LlmBudget = budget }, CancellationToken.None);

        _sqlGeneration.Verify(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        outcome.Succeeded.Should().BeFalse();
        outcome.FinalSql.Should().Be(GeneratedSql);
        outcome.Repairs.Should().BeEmpty();
        outcome.Text.Should().Contain("**Execution Error:** 42601: syntax error at end of input");
        budget.Used.Should().Be(1);
    }

    [Test]
    public async Task LlmBudget_WithRoomForOneRepair_SpendsItAndSkipsTheNext()
    {
        _executor
            .Setup(x => x.DryRunAsync(DataSourceId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("dry-run error");
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CorrectedSql);
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, It.IsAny<string>(), 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult(null, "42601: syntax error at end of input", 0, false));
        var budget = new AskLlmBudget(2);

        var outcome = await CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object,
            new AskSqlPipelineOptions { LlmBudget = budget }, CancellationToken.None);

        _sqlGeneration.Verify(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        outcome.Repairs.Should().ContainSingle(x => x.Trigger == "dry-run");
        outcome.FinalSql.Should().Be(CorrectedSql);
        budget.Used.Should().Be(2);
    }

    [Test]
    public async Task LlmBudget_SpentBeforeGeneration_FailsWithTheBudgetMessage()
    {
        var budget = new AskLlmBudget(1);
        budget.TryConsume().Should().BeTrue();

        var act = () => CreatePipeline().RunAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, _executor.Object,
            new AskSqlPipelineOptions { LlmBudget = budget }, CancellationToken.None);

        (await act.Should().ThrowAsync<AskLlmBudgetExceededException>())
            .Which.Message.Should().Contain("limit of 1 LLM call");
        _sqlGeneration.Verify(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>(), It.IsAny<decimal?>()), Times.Never);
    }

    [Test]
    public async Task ExecuteAsync_TimeBudgetElapses_ReturnsAnAuditedAndSignalledError()
    {
        var auditLogs = new List<McpAuditLog>();
        var signals = new List<McpQuerySignal>();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var llmCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = CancellationToken.None;
        _llmProvider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (LlmRequest _, CancellationToken ct) =>
            {
                observed = ct;
                llmCalled.SetResult();
                await Task.Delay(Timeout.Infinite, ct);

                return new Beacon.Core.Models.Ai.LlmResponse { Content = "DATA" };
            });
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(x => x.GetService(typeof(ILlmProvider))).Returns(_llmProvider.Object);
        var tool = CreateAuditedTool(auditLogs, signals, serviceProvider, ClassifierCallingTheProvider(), timeProvider: clock);

        var call = tool.ExecuteAsync(Question, cancellationToken: CancellationToken.None);
        await llmCalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(120));
        var result = await call.WaitAsync(TimeSpan.FromSeconds(10));

        (result.IsError ?? false).Should().BeTrue();
        var error = result.Content.OfType<TextContentBlock>().Single().Text;
        error.Should().Be("ask exceeded its time budget of 120 s; narrow the question or ask again.");
        observed.IsCancellationRequested.Should().BeTrue("the LLM call runs under the ask's own budget");
        var audit = auditLogs.Should().ContainSingle().Subject;
        audit.ProjectId.Should().Be(ProjectId);
        audit.ErrorMessage.Should().Be(error);
        signals.Should().ContainSingle().Which.ExecutionFailed.Should().BeTrue();
    }

    [Test]
    public async Task ExecuteAsync_ConfiguredTimeBudget_HandsTheSqlPathAToken_ThatFiresAfterIt()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(x => x.GetService(typeof(ILlmProvider))).Returns(_llmProvider.Object);
        var intentClassifier = new Mock<IIntentClassifier>();
        intentClassifier
            .Setup(x => x.ClassifyAsync(It.IsAny<ILlmProvider>(), Question, It.IsAny<CancellationToken>()))
            .ReturnsAsync(IntentClassification.DataQuery);
        var knowledgeGraph = new Mock<IKnowledgeGraphService>();
        knowledgeGraph
            .Setup(x => x.GetProjectDataSourcesAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DataSourceKnowledge { DataSourceId = DataSourceId, Name = "orders" }]);
        var router = new Mock<IDataSourceRouter>();
        router
            .Setup(x => x.RouteAsync(It.IsAny<ILlmProvider>(), It.IsAny<List<DataSourceKnowledge>>(), Question, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoutingResult { Sources = [new RoutedSource { DataSourceId = DataSourceId, DataSourceName = "orders" }] });
        var pipelineEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handed = CancellationToken.None;
        AskSqlPipelineOptions? handedOptions = null;
        var pipeline = new Mock<IAskSqlPipeline>();
        pipeline
            .Setup(x => x.RunAsync(It.IsAny<ILlmProvider>(), DataSourceId, ProjectId, Question, It.IsAny<McpSettingsData>(),
                It.IsAny<IAskSqlExecutor>(), It.IsAny<AskSqlPipelineOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async (ILlmProvider _, int _, int _, string _, McpSettingsData _, IAskSqlExecutor _, AskSqlPipelineOptions options, CancellationToken ct) =>
            {
                handed = ct;
                handedOptions = options;
                pipelineEntered.SetResult();
                await Task.Delay(Timeout.Infinite, ct);

                return null!;
            });
        var deployment = new McpDeploymentOptions { Ceilings = new McpCeilingOptions { AskTimeoutSeconds = 5 } };
        var tool = CreateAuditedTool([], [], serviceProvider, intentClassifier, deployment, timeProvider: clock,
            pipeline: pipeline.Object, knowledgeGraph: knowledgeGraph.Object, router: router.Object);

        var call = tool.ExecuteAsync(Question, cancellationToken: CancellationToken.None);
        await pipelineEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(4));
        handed.IsCancellationRequested.Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(1));
        var result = await call.WaitAsync(TimeSpan.FromSeconds(10));

        handed.IsCancellationRequested.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("time budget of 5 s");
        handedOptions!.LlmBudget.Should().NotBeNull();
        handedOptions.LlmBudget!.MaxCalls.Should().Be(12);
    }

    [Test]
    public async Task ExecuteAsync_CallerCancels_PropagatesTheCancellation_AfterAuditing()
    {
        var auditLogs = new List<McpAuditLog>();
        var signals = new List<McpQuerySignal>();
        var llmCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _llmProvider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (LlmRequest _, CancellationToken ct) =>
            {
                llmCalled.SetResult();
                await Task.Delay(Timeout.Infinite, ct);

                return new Beacon.Core.Models.Ai.LlmResponse { Content = "DATA" };
            });
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(x => x.GetService(typeof(ILlmProvider))).Returns(_llmProvider.Object);
        var tool = CreateAuditedTool(auditLogs, signals, serviceProvider, ClassifierCallingTheProvider(),
            timeProvider: new Microsoft.Extensions.Time.Testing.FakeTimeProvider());
        using var caller = new CancellationTokenSource();

        var call = tool.ExecuteAsync(Question, cancellationToken: caller.Token);
        await llmCalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await caller.CancelAsync();

        await FluentActions.Awaiting(() => call).Should().ThrowAsync<OperationCanceledException>();
        auditLogs.Should().ContainSingle().Which.ErrorMessage.Should().Be("Cancelled by the caller.");
        auditLogs.Single().ErrorMessage.Should().NotContain("budget");
        signals.Should().ContainSingle().Which.ExecutionFailed.Should().BeTrue();
    }

    [Test]
    public async Task ExecuteAsync_LlmBudgetSpentBeforeRouting_FailsAuditedAndSignalled()
    {
        var auditLogs = new List<McpAuditLog>();
        var signals = new List<McpQuerySignal>();
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(x => x.GetService(typeof(ILlmProvider))).Returns(_llmProvider.Object);
        var intentClassifier = new Mock<IIntentClassifier>();
        intentClassifier
            .Setup(x => x.ClassifyAsync(It.IsAny<ILlmProvider>(), Question, It.IsAny<CancellationToken>()))
            .ReturnsAsync(IntentClassification.DataQuery);
        var knowledgeGraph = new Mock<IKnowledgeGraphService>();
        knowledgeGraph
            .Setup(x => x.GetProjectDataSourcesAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new DataSourceKnowledge { DataSourceId = 1, Name = "orders" },
                new DataSourceKnowledge { DataSourceId = 2, Name = "billing" }
            ]);
        var router = new Mock<IDataSourceRouter>();
        var deployment = new McpDeploymentOptions { Ceilings = new McpCeilingOptions { AskMaxLlmCalls = 1 } };
        var tool = CreateAuditedTool(auditLogs, signals, serviceProvider, intentClassifier, deployment,
            knowledgeGraph: knowledgeGraph.Object, router: router.Object);

        var result = await tool.ExecuteAsync(Question, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeTrue();
        var error = result.Content.OfType<TextContentBlock>().Single().Text;
        error.Should().Be("ask reached its limit of 1 LLM call before it could answer; narrow the question or ask again.");
        router.Verify(x => x.RouteAsync(It.IsAny<ILlmProvider>(), It.IsAny<List<DataSourceKnowledge>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        auditLogs.Should().ContainSingle().Which.ErrorMessage.Should().Be(error);
        signals.Should().ContainSingle().Which.ExecutionFailed.Should().BeTrue();
    }

    [Test]
    public async Task ExecuteAsync_KnowledgeQuestion_TakesACallForTheAnswer_AndFailsAuditedWithoutOne()
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
        var deployment = new McpDeploymentOptions { Ceilings = new McpCeilingOptions { AskMaxLlmCalls = 1 } };
        var tool = CreateAuditedTool(auditLogs, signals, serviceProvider, intentClassifier, deployment, knowledgeAnswer: knowledgeAnswer.Object);

        var result = await tool.ExecuteAsync(Question, cancellationToken: CancellationToken.None);

        var error = result.Content.OfType<TextContentBlock>().Single().Text;
        error.Should().Contain("limit of 1 LLM call");
        knowledgeAnswer.Verify(x => x.AnswerAsync(It.IsAny<ILlmProvider>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>()), Times.Never);
        auditLogs.Should().ContainSingle().Which.ErrorMessage.Should().Be(error);
        signals.Should().ContainSingle().Which.ExecutionFailed.Should().BeTrue();
    }

    [Test]
    public async Task ExecuteAsync_OneDataSource_SpendsNoCallOnRouting_AndHandsThePipelineTheCallsBudget()
    {
        AskSqlPipelineOptions? handed = null;
        var pipeline = new Mock<IAskSqlPipeline>();
        pipeline
            .Setup(x => x.RunAsync(It.IsAny<ILlmProvider>(), DataSourceId, ProjectId, Question, It.IsAny<McpSettingsData>(),
                It.IsAny<IAskSqlExecutor>(), It.IsAny<AskSqlPipelineOptions>(), It.IsAny<CancellationToken>()))
            .Callback<ILlmProvider, int, int, string, McpSettingsData, IAskSqlExecutor, AskSqlPipelineOptions, CancellationToken>((_, _, _, _, _, _, options, _) => handed = options)
            .ReturnsAsync(CannedOutcome());
        var router = new Mock<IDataSourceRouter>();
        router
            .Setup(x => x.RouteAsync(It.IsAny<ILlmProvider>(), It.IsAny<List<DataSourceKnowledge>>(), Question, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoutingResult { Sources = [new RoutedSource { DataSourceId = DataSourceId, DataSourceName = "orders" }] });
        var tool = DataQueryTool([DataSourceId], pipeline: pipeline.Object, router: router.Object);

        var result = await tool.ExecuteAsync(Question, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        handed!.LlmBudget.Should().NotBeNull();
        handed.LlmBudget!.MaxCalls.Should().Be(12);
        handed.LlmBudget.Used.Should().Be(1, "the intent classification only: one source needs no routing call");
    }

    [Test]
    public async Task ExecuteAsync_SeveralDataSources_HandsTheCrossSourceQueryTheCallsBudget_AfterRouting()
    {
        AskLlmBudget? handed = null;
        var crossSource = new Mock<ICrossSourceQueryService>();
        crossSource
            .Setup(x => x.ExecuteAsync(It.IsAny<ILlmProvider>(), It.IsAny<List<RoutedSource>>(), ProjectId, Question, It.IsAny<McpSettingsData>(),
                true, It.IsAny<McpSignalBuilder>(), It.IsAny<AskLlmBudget?>(), It.IsAny<CancellationToken>()))
            .Callback<ILlmProvider, List<RoutedSource>, int, string, McpSettingsData, bool, McpSignalBuilder, AskLlmBudget?, CancellationToken>((_, _, _, _, _, _, _, budget, _) => handed = budget)
            .ReturnsAsync(("joined", true));
        var router = new Mock<IDataSourceRouter>();
        router
            .Setup(x => x.RouteAsync(It.IsAny<ILlmProvider>(), It.IsAny<List<DataSourceKnowledge>>(), Question, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoutingResult
            {
                Sources =
                [
                    new RoutedSource { DataSourceId = 1, DataSourceName = "orders" },
                    new RoutedSource { DataSourceId = 2, DataSourceName = "billing" }
                ]
            });
        var tool = DataQueryTool([1, 2], router: router.Object, crossSource: crossSource.Object);

        var result = await tool.ExecuteAsync(Question, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        handed.Should().NotBeNull();
        handed!.MaxCalls.Should().Be(12);
        handed.Used.Should().Be(2, "the intent classification and the routing call");
    }

    [Test]
    public async Task ExecuteAsync_TimeBudgetFiresAndTheFailureIsWrapped_StillAnswersTheTimeBudget()
    {
        var auditLogs = new List<McpAuditLog>();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var pipeline = new Mock<IAskSqlPipeline>();
        pipeline
            .Setup(x => x.RunAsync(It.IsAny<ILlmProvider>(), DataSourceId, ProjectId, Question, It.IsAny<McpSettingsData>(),
                It.IsAny<IAskSqlExecutor>(), It.IsAny<AskSqlPipelineOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() => clock.Advance(TimeSpan.FromSeconds(120)))
            .ThrowsAsync(new InvalidOperationException("provider failed", new OperationCanceledException()));
        var tool = DataQueryTool([DataSourceId], auditLogs, pipeline: pipeline.Object, timeProvider: clock);

        var result = await tool.ExecuteAsync(Question, cancellationToken: CancellationToken.None);

        var error = result.Content.OfType<TextContentBlock>().Single().Text;
        error.Should().Be("ask exceeded its time budget of 120 s; narrow the question or ask again.");
        auditLogs.Should().ContainSingle().Which.ErrorMessage.Should().Be(error);
    }

    [Test]
    public async Task ExecuteAsync_TimeBudgetFiresWhileTheStatementRuns_AnswersTheTimeBudget_NotTheExecutionError()
    {
        var auditLogs = new List<McpAuditLog>();
        var signals = new List<McpQuerySignal>();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        SetupRealPipelineExecution(() => clock.Advance(TimeSpan.FromSeconds(120)));
        var tool = DataQueryTool([DataSourceId], auditLogs, signals, pipeline: CreatePipeline(), timeProvider: clock);

        var result = await tool.ExecuteAsync(Question, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Be("ask exceeded its time budget of 120 s; narrow the question or ask again.");
        auditLogs.Should().ContainSingle().Which.ErrorMessage.Should().Contain("time budget");
        signals.Should().ContainSingle().Which.ExecutionFailed.Should().BeTrue();
    }

    [Test]
    public async Task ExecuteAsync_CallerCancelsWhileTheStatementRuns_PropagatesTheCancellation_AfterAuditing()
    {
        var auditLogs = new List<McpAuditLog>();
        using var caller = new CancellationTokenSource();
        SetupRealPipelineExecution(caller.Cancel);
        var tool = DataQueryTool([DataSourceId], auditLogs, pipeline: CreatePipeline());

        var act = () => tool.ExecuteAsync(Question, cancellationToken: caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        auditLogs.Should().ContainSingle().Which.ErrorMessage.Should().Be("Cancelled by the caller.");
    }

    [Test]
    public async Task ExecuteAsync_QuestionSpanningMoreThanFourSources_FailsAuditedAndSignalled_BeforeAnyGeneration()
    {
        var auditLogs = new List<McpAuditLog>();
        var signals = new List<McpQuerySignal>();
        RoutesTo(1, 1, 2, 3, 4, 5);
        var pipeline = new Mock<IAskSqlPipeline>();
        var crossSource = new Mock<ICrossSourceQueryService>();
        var tool = DataQueryTool([1, 2, 3, 4, 5], auditLogs, signals, pipeline: pipeline.Object,
            router: new DataSourceRouter(NullLogger<DataSourceRouter>.Instance), crossSource: crossSource.Object);

        var result = await tool.ExecuteAsync(Question, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeTrue();
        var error = result.Content.OfType<TextContentBlock>().Single().Text;
        error.Should().Be("This question spans 5 data sources; ask can join at most 4. Narrow the question to fewer sources.");
        auditLogs.Should().ContainSingle().Which.ErrorMessage.Should().Be(error);
        signals.Should().ContainSingle().Which.ExecutionFailed.Should().BeTrue();
        crossSource.VerifyNoOtherCalls();
        pipeline.VerifyNoOtherCalls();
    }

    [Test]
    public async Task ExecuteAsync_QuestionSpanningFourSourcesNamedTwice_IsJoined()
    {
        RoutesTo(1, 2, 3, 4, 4, 1);
        var crossSource = new Mock<ICrossSourceQueryService>();
        crossSource
            .Setup(x => x.ExecuteAsync(It.IsAny<ILlmProvider>(), It.Is<List<RoutedSource>>(y => y.Count == 4), ProjectId, Question,
                It.IsAny<McpSettingsData>(), true, It.IsAny<McpSignalBuilder>(), It.IsAny<AskLlmBudget?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("joined", true));
        var tool = DataQueryTool([1, 2, 3, 4, 5], router: new DataSourceRouter(NullLogger<DataSourceRouter>.Instance), crossSource: crossSource.Object);

        var result = await tool.ExecuteAsync(Question, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        crossSource.Verify(x => x.ExecuteAsync(It.IsAny<ILlmProvider>(), It.Is<List<RoutedSource>>(y => y.Count == 4), ProjectId, Question,
            It.IsAny<McpSettingsData>(), true, It.IsAny<McpSignalBuilder>(), It.IsAny<AskLlmBudget?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task GenerateAndExecuteSqlAsync_ExecutionErrorWithoutAnLlmCallForTheRepair_IsSignalledRedacted()
    {
        SetupCleanDryRun();
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult(null, ErrorQuotingAValue, 0, false));

        var signal = await RunToolPipelineAsync(new AskLlmBudget(2), spendFirst: 1);

        signal.ExecutionFailed.Should().BeTrue();
        signal.ExecutionError.Should().Be(RedactedError);
        signal.IsSuccessful.Should().BeFalse();
        _sqlGeneration.Verify(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task GenerateAndExecuteSqlAsync_DryRunErrorWithoutAnLlmCallForTheRepair_IsSignalledRedacted()
    {
        _executor
            .Setup(x => x.DryRunAsync(DataSourceId, GeneratedSql, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ErrorQuotingAValue);
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (1 rows)\n", null, 1, true));

        var signal = await RunToolPipelineAsync(new AskLlmBudget(2), spendFirst: 1);

        signal.DryRunFailed.Should().BeTrue();
        signal.DryRunError.Should().Be(RedactedError);
        signal.ExecutionFailed.Should().BeFalse();
    }

    [Test]
    public async Task GenerateAndExecuteSqlAsync_SchemaErrorWithoutAnLlmCallForTheRepair_IsSignalled()
    {
        SetupCleanDryRun();
        UseCatalogOfOrdersWithId();
        GeneratesForAnySettings("SELECT bogus FROM orders");
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, "SELECT bogus FROM orders", 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (1 rows)\n", null, 1, true));

        var signal = await RunToolPipelineAsync(new AskLlmBudget(2), spendFirst: 1);

        signal.SchemaValidationFailed.Should().BeTrue();
        signal.SchemaValidationError.Should().Contain("bogus");
    }

    // Without an LLM budget: the schema and dry-run repairs spend both attempts, so the execution error that follows is
    // recorded without a repair.
    [Test]
    public async Task GenerateAndExecuteSqlAsync_ExecutionErrorAfterTheRepairAttemptsAreSpent_IsSignalledRedacted()
    {
        UseCatalogOfOrdersWithId();
        GeneratesForAnySettings("SELECT bogus FROM orders");
        _sqlGeneration
            .Setup(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("SELECT id FROM orders");
        _executor
            .Setup(x => x.DryRunAsync(DataSourceId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("42601: syntax error");
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, It.IsAny<string>(), 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult(null, ErrorQuotingAValue, 0, false));

        var signal = await RunToolPipelineAsync(null, spendFirst: 0);

        signal.SchemaValidationFailed.Should().BeTrue();
        signal.DryRunFailed.Should().BeTrue();
        signal.ExecutionFailed.Should().BeTrue();
        signal.ExecutionError.Should().Be(RedactedError);
        _sqlGeneration.Verify(x => x.RetryWithErrorAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // Runs the tool's single-source path over the real pipeline (the budget, if any, with some calls already spent, as the
    // intent classification spends one) and returns the signal it built.
    private async Task<McpQuerySignal> RunToolPipelineAsync(AskLlmBudget? budget, int spendFirst)
    {
        for (var i = 0; i < spendFirst; i++)
        {
            budget!.Consume();
        }

        var tool = CreateAuditedTool([], [], new Mock<IServiceProvider>(), new Mock<IIntentClassifier>(), pipeline: CreatePipeline());
        var signal = new McpSignalBuilder().SetTool("ask").SetQuestion(Question);

        await tool.GenerateAndExecuteSqlAsync(
            _llmProvider.Object, DataSourceId, ProjectId, Question, _settings, true, signal, budget, CancellationToken.None);

        return signal
            .SetResult(null, 1, false)
            .Build();
    }

    private void UseCatalogOfOrdersWithId()
    {
        _knowledgeGraph
            .Setup(x => x.GetSmartContextForAskAsync(DataSourceId, ProjectId, It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SmartSchemaContext
            {
                FullContext = "schema-context",
                DatabaseDialect = "PostgreSQL",
                TotalTableCount = 1,
                SchemaCatalog = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["orders"] = new(StringComparer.OrdinalIgnoreCase) { "id" }
                }
            });
    }

    private void GeneratesForAnySettings(string sql)
    {
        _sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(sql, ["orders"]));
    }

    private void RoutesTo(params int[] dataSourceIds)
    {
        var entries = string.Join(",", dataSourceIds.Select(x => $$"""{ "datasource_id": {{x}}, "datasource_name": "n", "reason": "r" }"""));
        _llmProvider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Beacon.Core.Models.Ai.LlmResponse { Content = $$"""{ "sources": [{{entries}}] }""" });
    }

    // The real pipeline over a statement whose execution comes back failed (as a provider reports a cancelled statement)
    // after the given action ran; its repair produces nothing.
    private void SetupRealPipelineExecution(Action whileRunning)
    {
        SetupCleanDryRun();
        _sqlGeneration
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult(GeneratedSql, ["orders"]));
        _executor
            .Setup(x => x.ExecuteAsync(DataSourceId, GeneratedSql, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(whileRunning)
            .ReturnsAsync(new AskExecutionResult(null, "57014: canceling statement due to user request", 0, false));
    }

    private ProjectAskTool DataQueryTool(
        int[] dataSourceIds,
        List<McpAuditLog>? auditLogs = null,
        List<McpQuerySignal>? signals = null,
        IAskSqlPipeline? pipeline = null,
        IDataSourceRouter? router = null,
        ICrossSourceQueryService? crossSource = null,
        TimeProvider? timeProvider = null)
    {
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(x => x.GetService(typeof(ILlmProvider))).Returns(_llmProvider.Object);
        var intentClassifier = new Mock<IIntentClassifier>();
        intentClassifier
            .Setup(x => x.ClassifyAsync(It.IsAny<ILlmProvider>(), Question, It.IsAny<CancellationToken>()))
            .ReturnsAsync(IntentClassification.DataQuery);
        _knowledgeGraph
            .Setup(x => x.GetProjectDataSourcesAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dataSourceIds
                .Select(x =>
                    new DataSourceKnowledge
                    {
                        DataSourceId = x,
                        Name = $"source {x}"
                    })
                .ToList());

        if (router == null)
        {
            var singleSource = new Mock<IDataSourceRouter>();
            singleSource
                .Setup(x => x.RouteAsync(It.IsAny<ILlmProvider>(), It.IsAny<List<DataSourceKnowledge>>(), Question, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RoutingResult { Sources = [new RoutedSource { DataSourceId = DataSourceId, DataSourceName = "orders" }] });
            router = singleSource.Object;
        }

        return CreateAuditedTool(auditLogs ?? [], signals ?? [], serviceProvider, intentClassifier, timeProvider: timeProvider,
            pipeline: pipeline, knowledgeGraph: _knowledgeGraph.Object, router: router, crossSource: crossSource);
    }

    private static AskSqlOutcome CannedOutcome()
    {
        return new AskSqlOutcome(GeneratedSql, GeneratedSql, ["orders"], true, "### Results (1 rows)\n", null, null, null, null,
            [], false, null, [], null, [], null, []);
    }

    private static Mock<IIntentClassifier> ClassifierCallingTheProvider()
    {
        var intentClassifier = new Mock<IIntentClassifier>();
        intentClassifier
            .Setup(x => x.ClassifyAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (ILlmProvider provider, string _, CancellationToken ct) =>
            {
                await provider.CompleteAsync(new LlmRequest(), ct);

                return IntentClassification.DataQuery;
            });

        return intentClassifier;
    }

    private void SetupCleanDryRun()
    {
        _executor
            .Setup(x => x.DryRunAsync(DataSourceId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
    }

    private AskSqlPipeline CreatePipeline(ILogger<AskSqlPipeline>? logger = null, bool forbidRowData = false)
    {
        return new AskSqlPipeline(
            _knowledgeGraph.Object,
            _sqlGeneration.Object,
            TestSqlGate.Create(_guardrail.Object),
            TestRowSecurity.Resolver,
            TestRowDataEgress.Discarding(),
            Options.Create(new McpDeploymentOptions { ForbidRowDataToLlm = forbidRowData }),
            new McpStatementLog(),
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
        IKnowledgeAnswerService? knowledgeAnswer = null,
        TimeProvider? timeProvider = null,
        IAskSqlPipeline? pipeline = null,
        IKnowledgeGraphService? knowledgeGraph = null,
        IDataSourceRouter? router = null,
        ICrossSourceQueryService? crossSource = null)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AuditCaptureContext(auditLogs, signals));
        var settingsProvider = SettingsProviderMock.Create();

        return new ProjectAskTool(
            knowledgeGraph ?? Mock.Of<IKnowledgeGraphService>(),
            settingsProvider.Object,
            SettingsProviderMock.Unshared(settingsProvider.Object),
            serviceProvider.Object,
            new McpProjectContext { UserId = userId, AllowedProjectIds = [ProjectId], QueryProjectIds = [ProjectId] },
            new McpAuditService(factory.Object, settingsProvider.Object, new HttpContextAccessor(), Options.Create(new McpDeploymentOptions()), NullLogger<McpAuditService>.Instance, new McpAuditOutcome(), Options.Create(new BeaconTelemetryOptions()), NullLoggerFactory.Instance, TestMcpAudit.ContentHasher, new McpStatementLog()),
            new McpSignalService(factory.Object, settingsProvider.Object, NullLogger<McpSignalService>.Instance),
            pipeline ?? Mock.Of<IAskSqlPipeline>(),
            _executor.Object,
            intentClassifier.Object,
            router ?? Mock.Of<IDataSourceRouter>(),
            knowledgeAnswer ?? Mock.Of<IKnowledgeAnswerService>(),
            crossSource ?? Mock.Of<ICrossSourceQueryService>(),
            Options.Create(deploymentOptions ?? new McpDeploymentOptions()),
            new McpQueryConcurrencyLimiter(),
            timeProvider ?? TimeProvider.System,
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

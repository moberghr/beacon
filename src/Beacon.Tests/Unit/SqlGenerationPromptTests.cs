using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Ai;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// Prompt-assembly tests for <see cref="SqlGenerationService"/> (§ Architecture, item 2): the user
/// message carries a <c>TimeProvider</c>-sourced "TODAY (UTC)" anchor so relative-time questions
/// ("last week", "this quarter") are not guessed, the default system prompt names the dialect rule,
/// a custom operator-owned <c>AskSystemPrompt</c> is left untouched, and the token cap is raised to
/// 2048 across generation, candidate sampling and repair (SC2).
/// </summary>
[TestFixture]
public class SqlGenerationPromptTests
{
    private const string SchemaContext = "TABLE sales.orders (id, status)";
    private const string Question = "orders placed last week";
    private const string RepairTablesContext = "TABLE sales.orders (id)";

    private FakeTimeProvider _clock = null!;
    private Mock<ILlmProvider> _llmProvider = null!;
    private McpSettingsData _settings = null!;
    private List<LlmRequest> _capturedRequests = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero));
        _settings = new McpSettingsData();
        _capturedRequests = [];

        _llmProvider = new Mock<ILlmProvider>();
        _llmProvider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LlmRequest, CancellationToken>((request, _) => _capturedRequests.Add(request))
            .ReturnsAsync(new LlmResponse { Content = "SELECT id FROM sales.orders;" });
    }

    [Test]
    public async Task GenerateAsync_UserMessage_ContainsTodayLineFromTimeProvider()
    {
        var service = new SqlGenerationService(_clock);

        await service.GenerateAsync(_llmProvider.Object, SchemaContext, Question, _settings, CancellationToken.None);

        var userMessage = _capturedRequests.Single().Messages[^1].Content;
        userMessage.Should().Contain("TODAY (UTC): 2026-09-04 (Friday)");
    }

    // The stable part — the operator's instruction and the schema — is a user message marked as a cache breakpoint; the date
    // and the question, which change, follow in an uncached last message.
    [Test]
    public async Task GenerateAsync_SchemaContext_IsInACachedUserMessage_AndTheDateAndQuestionInTheLastMessage()
    {
        _settings.GlobalInstruction = "Amounts are in EUR.";
        var service = new SqlGenerationService(_clock);

        await service.GenerateAsync(_llmProvider.Object, SchemaContext, Question, _settings, CancellationToken.None);

        var request = _capturedRequests.Single();
        request.Messages.Should().HaveCount(2);
        request.Messages.Should().OnlyContain(x => x.Role == ConversationRole.User);
        request.Messages[0].CacheBreakpoint.Should().BeTrue();
        request.Messages[0].Content.Should().Be($"INSTRUCTIONS:\nAmounts are in EUR.\n\nDATABASE CONTEXT:\n{SchemaContext}");
        request.Messages[1].CacheBreakpoint.Should().BeFalse();
        request.Messages[1].Content.Should().Be($"TODAY (UTC): 2026-09-04 (Friday)\n\nUSER QUESTION: {Question}");
        request.Operation.Should().Be(LlmOperations.SqlGeneration);
    }

    // Schema text (database comments, documentation, glossary) is user content: it never reaches the system prompt.
    [Test]
    public async Task SchemaContext_NeverAppearsInTheSystemPrompt()
    {
        var service = new SqlGenerationService(_clock);

        await service.GenerateAsync(_llmProvider.Object, SchemaContext, Question, _settings, CancellationToken.None);
        await service.GenerateCandidatesAsync(_llmProvider.Object, SchemaContext, Question, _settings, candidateCount: 2, temperature: 0.7m, CancellationToken.None);
        await service.RetryWithErrorAsync(
            _llmProvider.Object, "system prompt", "SELECT bogus FROM sales.orders;", "column \"bogus\" does not exist",
            SchemaContext, "TABLE sales.orders (id)", Question, CancellationToken.None);

        _capturedRequests.Should().HaveCount(4);
        _capturedRequests.Should().OnlyContain(x => !x.SystemPrompt!.Contains(SchemaContext));
        _capturedRequests.Should().OnlyContain(x => !x.SystemPrompt!.Contains("DATABASE CONTEXT") && !x.SystemPrompt.Contains("FULL DATABASE SCHEMA"));
        _capturedRequests.Should().OnlyContain(x => !x.SystemPrompt!.Contains("TABLE sales.orders"));
    }

    [Test]
    public async Task GenerateAsync_NoTimeProviderSupplied_FallsBackToSystemClock()
    {
        // Optional ctor param defaults to null; the fallback must be TimeProvider.System, not a crash
        // or a fixed/omitted date.
        var service = new SqlGenerationService();
        var expectedToday = TimeProvider.System.GetUtcNow();

        await service.GenerateAsync(_llmProvider.Object, SchemaContext, Question, _settings, CancellationToken.None);

        var userMessage = _capturedRequests.Single().Messages[^1].Content;
        userMessage.Should().Contain($"TODAY (UTC): {expectedToday:yyyy-MM-dd} ({expectedToday.DayOfWeek})");
    }

    [Test]
    public async Task GenerateAsync_DefaultSystemPrompt_NamesDialectRule()
    {
        var service = new SqlGenerationService(_clock);

        await service.GenerateAsync(_llmProvider.Object, SchemaContext, Question, _settings, CancellationToken.None);

        _capturedRequests.Single().SystemPrompt.Should().Contain(
            "The data source header names the SQL dialect. Use only that dialect's syntax, quoting and date/time functions.");
    }

    [Test]
    public async Task GenerateAsync_CustomAskSystemPrompt_IsLeftUntouched()
    {
        _settings.AskSystemPrompt = "Custom operator prompt. Return only SQL.";
        var service = new SqlGenerationService(_clock);

        await service.GenerateAsync(_llmProvider.Object, SchemaContext, Question, _settings, CancellationToken.None);

        _capturedRequests.Single().SystemPrompt.Should().Be("Custom operator prompt. Return only SQL.");
    }

    [Test]
    public async Task GenerateAsync_MaxTokens_Is2048()
    {
        var service = new SqlGenerationService(_clock);

        await service.GenerateAsync(_llmProvider.Object, SchemaContext, Question, _settings, CancellationToken.None);

        _capturedRequests.Single().MaxTokens.Should().Be(2048);
    }

    [Test]
    public async Task GenerateCandidatesAsync_MaxTokens_Is2048_AndCarriesTodayLine()
    {
        var service = new SqlGenerationService(_clock);

        await service.GenerateCandidatesAsync(_llmProvider.Object, SchemaContext, Question, _settings, candidateCount: 2, temperature: 0.7m, CancellationToken.None);

        _capturedRequests.Should().HaveCount(2);
        _capturedRequests.Should().OnlyContain(x => x.MaxTokens == 2048);
        _capturedRequests.Should().OnlyContain(x => x.Messages[0].CacheBreakpoint && x.Operation == LlmOperations.SqlSelfConsistency);
        _capturedRequests.Should().OnlyContain(x => x.Messages.Last().Content.Contains("TODAY (UTC): 2026-09-04 (Friday)"));
    }

    [Test]
    public async Task RetryWithErrorAsync_MaxTokens_Is2048_AndCarriesTodayLine()
    {
        var service = new SqlGenerationService(_clock);

        await service.RetryWithErrorAsync(
            _llmProvider.Object,
            systemPrompt: "system prompt",
            previousSql: "SELECT bogus FROM sales.orders;",
            error: "column \"bogus\" does not exist",
            fullContext: SchemaContext,
            tablesContext: null,
            question: Question,
            CancellationToken.None);

        var request = _capturedRequests.Single();
        request.MaxTokens.Should().Be(2048);
        request.Messages[^1].Content.Should().Contain("TODAY (UTC): 2026-09-04 (Friday)");
    }

    // A repair over the context its generation was sent opens with the generation's first message, byte for byte and marked as
    // the same cache breakpoint; the question, the previous SQL, its error and the rules follow in the last message, which ends
    // with the instruction.
    [TestCase(true)]
    [TestCase(false)]
    public async Task RetryWithErrorAsync_OverTheGenerationContext_ReusesTheGenerationFirstMessage(bool withTables)
    {
        _settings.GlobalInstruction = "Amounts are in EUR.";
        var service = new SqlGenerationService(_clock);
        await service.GenerateAsync(_llmProvider.Object, SchemaContext, Question, _settings, CancellationToken.None);

        await service.RetryWithErrorAsync(
            _llmProvider.Object,
            systemPrompt: "system prompt",
            previousSql: "SELECT bogus FROM sales.orders;",
            error: "column \"bogus\" does not exist",
            fullContext: SchemaContext,
            tablesContext: withTables ? RepairTablesContext : null,
            question: Question,
            settings: _settings,
            generationContext: SchemaContext,
            CancellationToken.None);

        _capturedRequests.Should().HaveCount(2);
        var generation = _capturedRequests[0];
        var repair = _capturedRequests[1];
        repair.SystemPrompt.Should().Be("system prompt");
        repair.Operation.Should().Be(LlmOperations.SqlRepair);
        repair.Messages.Should().HaveCount(2);
        repair.Messages.Should().OnlyContain(x => x.Role == ConversationRole.User);
        repair.Messages[0].Should().Be(generation.Messages[0]);
        repair.Messages[0].Content.Should().Be($"INSTRUCTIONS:\nAmounts are in EUR.\n\nDATABASE CONTEXT:\n{SchemaContext}");
        repair.Messages[0].CacheBreakpoint.Should().BeTrue();
        repair.Messages[1].CacheBreakpoint.Should().BeFalse();
        repair.Messages[1].Content.Should().Be(ExpectedRepairAttempt(withTables));
    }

    // A repair over any other context — or one that does not say what its generation was sent — opens with the full schema
    // and sets no cache breakpoint; the last message is the same.
    [TestCase(true, true)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(false, false)]
    public async Task RetryWithErrorAsync_OverAnotherContext_SetsNoCacheBreakpoint(bool withTables, bool namesGenerationContext)
    {
        _settings.GlobalInstruction = "Amounts are in EUR.";
        var service = new SqlGenerationService(_clock);

        if (namesGenerationContext)
        {
            await service.RetryWithErrorAsync(
                _llmProvider.Object, "system prompt", "SELECT bogus FROM sales.orders;", "column \"bogus\" does not exist",
                SchemaContext, withTables ? RepairTablesContext : null, Question, _settings, SchemaContext + "\nTABLE sales.extra (id)", CancellationToken.None);
        }
        else
        {
            await service.RetryWithErrorAsync(
                _llmProvider.Object, "system prompt", "SELECT bogus FROM sales.orders;", "column \"bogus\" does not exist",
                SchemaContext, withTables ? RepairTablesContext : null, Question, CancellationToken.None);
        }

        var request = _capturedRequests.Single();
        request.SystemPrompt.Should().Be("system prompt");
        request.Operation.Should().Be(LlmOperations.SqlRepair);
        request.Messages.Should().HaveCount(2);
        request.Messages.Should().OnlyContain(x => x.Role == ConversationRole.User);
        request.Messages.Should().OnlyContain(x => !x.CacheBreakpoint);
        request.Messages[0].Content.Should().Be($"FULL DATABASE SCHEMA (authoritative source):\n{SchemaContext}");
        request.Messages[1].Content.Should().Be(ExpectedRepairAttempt(withTables));
    }

    [Test]
    public async Task GenerateAsync_DefaultSystemPrompt_NamesAssumptionsBlockRule()
    {
        var service = new SqlGenerationService(_clock);

        await service.GenerateAsync(_llmProvider.Object, SchemaContext, Question, _settings, CancellationToken.None);

        _capturedRequests.Single().SystemPrompt.Should().Contain("-- assumptions:");
        _capturedRequests.Single().SystemPrompt.Should().Contain("-- clarification:");
        _capturedRequests.Single().SystemPrompt.Should().NotContain("Return ONLY the SQL query, no explanation");
    }

    [Test]
    public async Task GenerateAsync_AssumptionsBlockThenSql_ParsesAssumptionsAndCleanSql()
    {
        _llmProvider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                Content = """
                    -- assumptions:
                    -- "last week" = the 7 days before today
                    -- revenue = SUM(order_items.quantity * order_items.unit_price)
                    SELECT id FROM sales.orders;
                    """
            });
        var service = new SqlGenerationService(_clock);

        var result = await service.GenerateAsync(_llmProvider.Object, SchemaContext, Question, _settings, CancellationToken.None);

        result.Sql.Should().Be("SELECT id FROM sales.orders;");
        result.Sql.Should().NotContain("--");
        result.Assumptions.Should().BeEquivalentTo(
        [
            "\"last week\" = the 7 days before today",
            "revenue = SUM(order_items.quantity * order_items.unit_price)"
        ]);
        result.ClarificationHint.Should().BeNull();
    }

    [Test]
    public async Task GenerateAsync_FencedAssumptionsBlockThenSql_ParsesAssumptionsAndCleanSql()
    {
        _llmProvider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                Content = """
                    ```sql
                    -- assumptions:
                    -- "last week" = the 7 days before today
                    SELECT id FROM sales.orders;
                    ```
                    """
            });
        var service = new SqlGenerationService(_clock);

        var result = await service.GenerateAsync(_llmProvider.Object, SchemaContext, Question, _settings, CancellationToken.None);

        result.Sql.Should().Be("SELECT id FROM sales.orders;");
        result.Assumptions.Should().ContainSingle().Which.Should().Be("\"last week\" = the 7 days before today");
    }

    [Test]
    public async Task GenerateAsync_AssumptionsNone_YieldsEmptyAssumptionsList()
    {
        _llmProvider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                Content = """
                    -- assumptions: none
                    SELECT id FROM sales.orders;
                    """
            });
        var service = new SqlGenerationService(_clock);

        var result = await service.GenerateAsync(_llmProvider.Object, SchemaContext, Question, _settings, CancellationToken.None);

        result.Assumptions.Should().NotBeNull();
        result.Assumptions.Should().BeEmpty();
        result.Sql.Should().Be("SELECT id FROM sales.orders;");
    }

    [Test]
    public async Task GenerateAsync_NoLeadingCommentBlock_BehavesAsPreChange()
    {
        // Plain SQL with no leading `--` lines must parse exactly as it did before this feature.
        var service = new SqlGenerationService(_clock);

        var result = await service.GenerateAsync(_llmProvider.Object, SchemaContext, Question, _settings, CancellationToken.None);

        result.Sql.Should().Be("SELECT id FROM sales.orders;");
        result.Assumptions.Should().BeEmpty();
        result.ClarificationHint.Should().BeNull();
    }

    [Test]
    public async Task GenerateAsync_LeadingBlankLineBeforeAssumptionsBlock_StillLiftsTheBlockOutOfTheSql()
    {
        // A leading newline must not leave the `--` lines inside the SQL — the regex guardrail would then
        // reject the query for not starting with SELECT.
        _llmProvider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                Content = "\n\n-- assumptions:\n-- \"last week\" = the 7 days before today\nSELECT id FROM sales.orders;"
            });
        var service = new SqlGenerationService(_clock);

        var result = await service.GenerateAsync(_llmProvider.Object, SchemaContext, Question, _settings, CancellationToken.None);

        result.Sql.Should().Be("SELECT id FROM sales.orders;");
        result.Assumptions.Should().ContainSingle().Which.Should().Be("\"last week\" = the 7 days before today");
    }

    [Test]
    public async Task GenerateAsync_ProseBeforeFencedAssumptionsBlock_StillLiftsTheBlockOutOfTheSql()
    {
        _llmProvider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                Content = """
                    Here is the query:

                    ```sql
                    -- assumptions:
                    -- "last week" = the 7 days before today
                    -- clarification: Calendar week or rolling 7 days?
                    SELECT id FROM sales.orders;
                    ```
                    """
            });
        var service = new SqlGenerationService(_clock);

        var result = await service.GenerateAsync(_llmProvider.Object, SchemaContext, Question, _settings, CancellationToken.None);

        result.Sql.Should().Be("SELECT id FROM sales.orders;");
        result.Sql.Should().NotContain("--");
        result.Assumptions.Should().ContainSingle().Which.Should().Be("\"last week\" = the 7 days before today");
        result.ClarificationHint.Should().Be("Calendar week or rolling 7 days?");
    }

    [Test]
    public async Task GenerateAsync_ProseWithoutFenceAndNoCommentBlock_BehavesAsPreChange()
    {
        _llmProvider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = "Here is the query: SELECT id FROM sales.orders;" });
        var service = new SqlGenerationService(_clock);

        var result = await service.GenerateAsync(_llmProvider.Object, SchemaContext, Question, _settings, CancellationToken.None);

        result.Sql.Should().Be("SELECT id FROM sales.orders;");
        result.Assumptions.Should().BeEmpty();
    }

    [Test]
    public async Task GenerateAsync_ClarificationLine_ParsesClarificationHint()
    {
        _llmProvider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                Content = """
                    -- assumptions:
                    -- "recent" = the last 30 days
                    -- clarification: Did you mean this quarter or last quarter?
                    SELECT id FROM sales.orders;
                    """
            });
        var service = new SqlGenerationService(_clock);

        var result = await service.GenerateAsync(_llmProvider.Object, SchemaContext, Question, _settings, CancellationToken.None);

        result.ClarificationHint.Should().Be("Did you mean this quarter or last quarter?");
        result.Assumptions.Should().ContainSingle().Which.Should().Be("\"recent\" = the last 30 days");
        result.Sql.Should().Be("SELECT id FROM sales.orders;");
    }

    [Test]
    public async Task RetryWithErrorAsync_LeadingCommentBlock_StripsBlockFromReturnedSql()
    {
        _llmProvider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                Content = """
                    -- assumptions:
                    -- corrected the column name
                    SELECT id FROM sales.orders;
                    """
            });
        var service = new SqlGenerationService(_clock);

        var repairedSql = await service.RetryWithErrorAsync(
            _llmProvider.Object,
            systemPrompt: "system prompt",
            previousSql: "SELECT bogus FROM sales.orders;",
            error: "column \"bogus\" does not exist",
            fullContext: SchemaContext,
            tablesContext: null,
            question: Question,
            CancellationToken.None);

        repairedSql.Should().Be("SELECT id FROM sales.orders;");
        repairedSql.Should().NotContain("--");
    }

    private static string ExpectedRepairAttempt(bool withTables)
    {
        var tables = withTables ? $"EXACT SCHEMAS FOR TABLES IN YOUR QUERY:\n{RepairTablesContext}\n\n" : "";

        return "TODAY (UTC): 2026-09-04 (Friday)\n"
            + "\n"
            + tables
            + $"USER QUESTION: {Question}\n"
            + "\n"
            + "Your previous SQL query failed.\n"
            + "\n"
            + "PREVIOUS SQL:\n"
            + "SELECT bogus FROM sales.orders;\n"
            + "\n"
            + "ERROR: column \"bogus\" does not exist\n"
            + "\n"
            + "CRITICAL RULES:\n"
            + "- Use ONLY column names listed in the schemas provided. Do NOT guess or infer column names.\n"
            + "- Check every column reference against the schema before including it.\n"
            + "- Use FK relationships shown in the schema to find correct join paths.\n"
            + "- Ensure non-aggregate SELECT columns appear in GROUP BY when using aggregate functions.\n"
            + "\n"
            + "Return ONLY the corrected SQL query.";
    }
}

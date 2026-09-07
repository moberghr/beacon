using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
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

        var userMessage = _capturedRequests.Single().Messages.Single().Content;
        userMessage.Should().Contain("TODAY (UTC): 2026-09-04 (Friday)");
    }

    [Test]
    public async Task GenerateAsync_TodayLine_PrecedesDatabaseContext()
    {
        var service = new SqlGenerationService(_clock);

        await service.GenerateAsync(_llmProvider.Object, SchemaContext, Question, _settings, CancellationToken.None);

        var userMessage = _capturedRequests.Single().Messages.Single().Content;
        var todayIndex = userMessage.IndexOf("TODAY (UTC):", StringComparison.Ordinal);
        var contextIndex = userMessage.IndexOf("DATABASE CONTEXT:", StringComparison.Ordinal);

        todayIndex.Should().BeGreaterThanOrEqualTo(0);
        contextIndex.Should().BeGreaterThan(todayIndex, "the date anchor must appear before the schema context so an operator-customised prompt still sees it first");
    }

    [Test]
    public async Task GenerateAsync_NoTimeProviderSupplied_FallsBackToSystemClock()
    {
        // Optional ctor param defaults to null; the fallback must be TimeProvider.System, not a crash
        // or a fixed/omitted date.
        var service = new SqlGenerationService();
        var expectedToday = TimeProvider.System.GetUtcNow();

        await service.GenerateAsync(_llmProvider.Object, SchemaContext, Question, _settings, CancellationToken.None);

        var userMessage = _capturedRequests.Single().Messages.Single().Content;
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
        _capturedRequests.Should().OnlyContain(x => x.Messages.Single().Content.Contains("TODAY (UTC): 2026-09-04 (Friday)"));
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
        request.Messages.Single().Content.Should().Contain("TODAY (UTC): 2026-09-04 (Friday)");
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
}

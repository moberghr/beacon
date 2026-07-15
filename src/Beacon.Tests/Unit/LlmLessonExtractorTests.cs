using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Learning;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;
using Beacon.Core.Services;

namespace Beacon.Tests.Unit;

/// <summary>
/// Unit coverage for the T2-B2 LLM-primary lesson extractor (§ Architecture ⑦). The extractor is the
/// PRIMARY path in schema-correction detection; <see cref="McpLearningAggregationService"/> falls back to
/// the deterministic regex path only when <see cref="LlmLessonExtractor.ExtractAsync"/> returns
/// <c>null</c>. These tests drive the real extractor against a mocked <see cref="ILlmProvider"/> (the
/// queue-backed provider is stubbed) to prove: (1) a well-formed JSON completion maps field-for-field to
/// an <see cref="ExtractedLesson"/> including the parsed <see cref="McpPatternType"/>; (2) unparseable
/// output yields <c>null</c> without throwing (so the caller can fall back); (3) a fenced ```json block is
/// tolerated; and (4) cancellation is propagated, never swallowed.
/// </summary>
[TestFixture]
public class LlmLessonExtractorTests
{
    private static readonly FailureCluster Cluster = new(
        DataSourceId: 1,
        SchemaName: "public",
        TableName: "loans",
        ColumnName: "created_at",
        Question: "How many loans were created last month?",
        GeneratedSql: "SELECT count(*) FROM public.loans WHERE created_at >= now() - interval '1 month'",
        Error: "column \"created_at\" does not exist",
        CorrectedSql: "SELECT count(*) FROM public.loans WHERE created_time >= now() - interval '1 month'",
        SchemaSlice: "loans(id, amount, created_time)");

    [Test]
    public async Task ExtractAsync_WellFormedJson_MapsEveryFieldIncludingPatternType()
    {
        const string json = """
            {
              "patternType": "JoinPattern",
              "patternContent": "NEVER use 'created_at' on public.loans — correct column is 'created_time'",
              "exampleQuestion": "How many loans were created last month?",
              "exampleSql": "SELECT count(*) FROM public.loans WHERE created_time >= now()",
              "symptom": "Query referenced a non-existent column created_at",
              "rootCause": "public.loans stores creation time as created_time, not created_at",
              "rule": "Use created_time for creation timestamps on public.loans",
              "exampleFix": "created_at -> created_time",
              "applicableWhen": "querying creation dates on public.loans"
            }
            """;

        var extractor = BuildExtractor(json);

        var lesson = await extractor.ExtractAsync(Cluster, CancellationToken.None);

        lesson.Should().NotBeNull();
        // PatternType must reflect the model's value, not the SchemaCorrection default — proves the parse.
        lesson!.PatternType.Should().Be(McpPatternType.JoinPattern);
        lesson.PatternContent.Should().Be("NEVER use 'created_at' on public.loans — correct column is 'created_time'");
        lesson.ExampleQuestion.Should().Be("How many loans were created last month?");
        lesson.ExampleSql.Should().Be("SELECT count(*) FROM public.loans WHERE created_time >= now()");
        lesson.Symptom.Should().Be("Query referenced a non-existent column created_at");
        lesson.RootCause.Should().Be("public.loans stores creation time as created_time, not created_at");
        lesson.Rule.Should().Be("Use created_time for creation timestamps on public.loans");
        lesson.ExampleFix.Should().Be("created_at -> created_time");
        lesson.ApplicableWhen.Should().Be("querying creation dates on public.loans");
    }

    [Test]
    public async Task ExtractAsync_UnknownPatternType_DefaultsToSchemaCorrection()
    {
        const string json = """
            { "patternType": "not-a-real-type", "patternContent": "check the schema for public.loans" }
            """;

        var extractor = BuildExtractor(json);

        var lesson = await extractor.ExtractAsync(Cluster, CancellationToken.None);

        lesson.Should().NotBeNull();
        lesson!.PatternType.Should().Be(McpPatternType.SchemaCorrection);
        lesson.PatternContent.Should().Be("check the schema for public.loans");
    }

    [Test]
    public async Task ExtractAsync_FencedJsonBlock_IsTolerated()
    {
        const string content = """
            Sure — here is the lesson you asked for:

            ```json
            {
              "patternType": "SchemaCorrection",
              "patternContent": "NEVER use 'created_at' on public.loans — use 'created_time'",
              "rule": "prefer created_time"
            }
            ```

            Hope that helps!
            """;

        var extractor = BuildExtractor(content);

        var lesson = await extractor.ExtractAsync(Cluster, CancellationToken.None);

        lesson.Should().NotBeNull();
        lesson!.PatternType.Should().Be(McpPatternType.SchemaCorrection);
        lesson.PatternContent.Should().Be("NEVER use 'created_at' on public.loans — use 'created_time'");
        lesson.Rule.Should().Be("prefer created_time");
    }

    [Test]
    public async Task ExtractAsync_NonJsonGarbage_ReturnsNullAndDoesNotThrow()
    {
        var extractor = BuildExtractor("I could not determine a lesson from this failure.");

        var act = async () => await extractor.ExtractAsync(Cluster, CancellationToken.None);

        var lesson = await act.Should().NotThrowAsync();
        lesson.Which.Should().BeNull();
    }

    [Test]
    public async Task ExtractAsync_MalformedJson_ReturnsNullAndDoesNotThrow()
    {
        // Has braces (so a JSON object is located) but is not valid JSON — must be swallowed to null so
        // the aggregation service falls back to the deterministic regex path.
        var extractor = BuildExtractor("{ this is not: valid json at all }");

        var lesson = await extractor.ExtractAsync(Cluster, CancellationToken.None);

        lesson.Should().BeNull();
    }

    [Test]
    public async Task ExtractAsync_EmptyContent_ReturnsNull()
    {
        var extractor = BuildExtractor("   ");

        var lesson = await extractor.ExtractAsync(Cluster, CancellationToken.None);

        lesson.Should().BeNull();
    }

    [Test]
    public async Task ExtractAsync_Cancellation_IsPropagatedNotSwallowed()
    {
        // The provider throwing OperationCanceledException must surface — cancellation is never turned into
        // a silent null (lesson from the Tier 0/1 review). NOTE: the assertion MUST be awaited — a bare
        // (non-awaited) ThrowAsync on a void test passes unconditionally (the discarded Task is never observed).
        var llm = new Mock<ILlmProvider>();
        llm.Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var extractor = new LlmLessonExtractor(llm.Object, NullLogger<LlmLessonExtractor>.Instance);

        var act = async () => await extractor.ExtractAsync(Cluster, new CancellationToken(true));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static LlmLessonExtractor BuildExtractor(string responseContent)
    {
        var llm = new Mock<ILlmProvider>();
        llm.Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = responseContent });

        return new LlmLessonExtractor(llm.Object, NullLogger<LlmLessonExtractor>.Instance);
    }
}

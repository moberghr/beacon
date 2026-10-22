using System.Globalization;
using Beacon.AI.Services.Learning;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Grounding;

/// <summary>
/// An auto-approved pattern stays auto-approved through a refresh only when its substance is unchanged: its content compared
/// exactly but for the statistics the detectors write into their own sentences (a count, an error rate — kept in the
/// pattern's signal count and confidence), its example SQL exactly but for its comments and the whitespace between its
/// tokens. A changed number in a lesson, whitespace inside a string literal, a changed number literal or an identifier's
/// case is a change: the pattern goes back to NeedsEvidence for the replay to measure again. The detectors run over
/// in-memory sets (no database — §4.7) with the real grounding SQL check (PostgreSQL).
/// </summary>
[TestFixture]
public class PatternContentComparisonTests
{
    private const int ProjectId = 1;
    private const int DataSourceId = TestGroundingSqlChecks.DataSourceId;
    private const string Question = "orders last week";

    // A lesson is prose a person approved as written: a changed number in it is a changed lesson.
    // Two signals against the stored lesson's one: more evidence than the lesson was extracted from, so the extractor is asked again.
    [TestCase("Divide total by 1000 to get dollars", McpPatternStatus.NeedsEvidence)]
    [TestCase("Divide total by 100 to get dollars", McpPatternStatus.AutoApproved)]
    public async Task ALessonWhoseNumberChanges_GoesBackToNeedsEvidence(string minedLesson, McpPatternStatus after)
    {
        var pattern = Pattern(McpPatternType.SchemaCorrection, "Divide total by 100 to get dollars", null, null, column: "amount");
        var extractor = new Mock<ILessonExtractor>();
        extractor.SetupGet(x => x.IsAvailable).Returns(true);
        extractor
            .Setup(x => x.ExtractAsync(It.IsAny<FailureCluster>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExtractedLesson(McpPatternType.SchemaCorrection, minedLesson, null, null, "symptom", "cause", "rule", null, null));
        var (service, context) = Build(pattern, extractor.Object);

        await service.DetectSchemaCorrectionsAsync(context, ProjectId, DataSourceId, [CorrectionSignal(), CorrectionSignal()], new McpLearningAggregationService.ExtractionStats(), retainContent: true, CancellationToken.None);

        pattern.Status.Should().Be(after);
        pattern.PatternContent.Should().Be(minedLesson);
    }

    // The same number of signals as the stored lesson was extracted from is no new evidence: no extractor call, no demotion.
    [Test]
    public async Task ALessonWhoseSignalCountIsUnchanged_IsNotExtractedAgain_AndStaysApproved()
    {
        var pattern = Pattern(McpPatternType.SchemaCorrection, "Divide total by 100 to get dollars", null, null, column: "amount");
        var extractor = new Mock<ILessonExtractor>();
        extractor.SetupGet(x => x.IsAvailable).Returns(true);
        var (service, context) = Build(pattern, extractor.Object);

        await service.DetectSchemaCorrectionsAsync(context, ProjectId, DataSourceId, [CorrectionSignal()], new McpLearningAggregationService.ExtractionStats(), retainContent: true, CancellationToken.None);

        extractor.Verify(x => x.ExtractAsync(It.IsAny<FailureCluster>(), It.IsAny<CancellationToken>()), Times.Never);
        pattern.Status.Should().Be(McpPatternStatus.AutoApproved);
        pattern.PatternContent.Should().Be("Divide total by 100 to get dollars");
    }

    // Example SQL compares exactly but for comments and the whitespace between tokens: a literal's whitespace, a number and an
    // identifier's case are the SQL itself.
    [TestCase("SELECT id FROM orders WHERE status = 'A  B'", "SELECT id FROM orders WHERE status = 'A B'", TestName = "WhitespaceInsideALiteral_IsAChange")]
    [TestCase("SELECT id FROM orders WHERE total = 1", "SELECT id FROM orders WHERE total = 2", TestName = "ANumberLiteral_IsAChange")]
    [TestCase("SELECT id FROM Orders", "SELECT id FROM orders", TestName = "AnIdentifiersCase_IsAChange")]
    [TestCase("SELECT \"Id\" FROM orders", "SELECT \"id\" FROM orders", TestName = "AQuotedIdentifiersCase_IsAChange")]
    [TestCase("SELECT id FROM orders WHERE status = 'a'", "SELECT id FROM orders WHERE status = 'A'", TestName = "ALiteralsCase_IsAChange")]
    public async Task AnAutoApprovedPattern_WhoseExampleSqlChangesInSubstance_GoesBackToNeedsEvidence(string storedSql, string minedSql)
    {
        var pattern = Pattern(McpPatternType.CommonQuery, CommonQueryContent(3), Question, storedSql);
        var stats = new McpLearningAggregationService.ExtractionStats();
        var (service, context) = Build(pattern);

        await service.DetectCommonQueriesAsync(context, ProjectId, DataSourceId, Signals(minedSql), stats, CancellationToken.None);

        pattern.Should().BeEquivalentTo(new { Status = McpPatternStatus.NeedsEvidence, ExampleSql = minedSql }, x => x.ExcludingMissingMembers());
        stats.Demotions.Should().Be(1);
    }

    [TestCase("SELECT id\n  FROM orders\tWHERE status = 'A B'", TestName = "WhitespaceBetweenTokens_IsNoChange")]
    [TestCase("SELECT id FROM orders /* the open ones */ WHERE status = 'A B' -- as asked", TestName = "Comments_AreNoChange")]
    public async Task AnAutoApprovedPattern_WhoseExampleSqlDiffersOnlyByCommentsOrWhitespace_StaysAutoApproved(string storedSql)
    {
        const string minedSql = "SELECT id FROM orders WHERE status = 'A B'";
        var pattern = Pattern(McpPatternType.CommonQuery, CommonQueryContent(3), Question, storedSql);
        var stats = new McpLearningAggregationService.ExtractionStats();
        var (service, context) = Build(pattern);

        await service.DetectCommonQueriesAsync(context, ProjectId, DataSourceId, Signals(minedSql), stats, CancellationToken.None);

        pattern.Should().BeEquivalentTo(new { Status = McpPatternStatus.AutoApproved, ExampleSql = minedSql }, x => x.ExcludingMissingMembers());
        stats.Demotions.Should().Be(0);
    }

    // Only the statistics in the detectors' own sentences are left out; the same sentence with anything else changed — or
    // with more said — is a change.
    [TestCase("Common query pattern for public.orders (12 similar queries)", McpPatternStatus.AutoApproved)]
    [TestCase("Common query pattern for public.customers (3 similar queries)", McpPatternStatus.NeedsEvidence)]
    [TestCase("Common query pattern for public.orders (3 similar queries); filter on status 2", McpPatternStatus.NeedsEvidence)]
    public async Task ACommonQueryPattern_WhoseCountAloneMoves_StaysAutoApproved(string storedContent, McpPatternStatus after)
    {
        var pattern = Pattern(McpPatternType.CommonQuery, storedContent, Question, "SELECT id FROM orders");
        var (service, context) = Build(pattern);

        await service.DetectCommonQueriesAsync(context, ProjectId, DataSourceId, Signals("SELECT id FROM orders"), new McpLearningAggregationService.ExtractionStats(), CancellationToken.None);

        pattern.Status.Should().Be(after);
        pattern.PatternContent.Should().Be(CommonQueryContent(3));
    }

    [TestCase("Tables public.customers + public.orders are frequently joined together (17 queries)", McpPatternStatus.AutoApproved)]
    [TestCase("Tables public.customers + public.order_lines are frequently joined together (3 queries)", McpPatternStatus.NeedsEvidence)]
    public async Task AJoinPattern_WhoseCountAloneMoves_StaysAutoApproved(string storedContent, McpPatternStatus after)
    {
        const string joinSql = "SELECT o.id FROM orders o JOIN customers c ON c.id = o.id";
        var pattern = Pattern(McpPatternType.JoinPattern, storedContent, Question, joinSql, table: "customers");
        var signals = Signals(joinSql, tables: "[\"public.orders\",\"public.customers\"]");
        var (service, context) = Build(pattern, check: TestGroundingSqlChecks.PassThrough().Object);

        await service.DetectJoinPatternsAsync(context, ProjectId, DataSourceId, signals, retainContent: true, new McpLearningAggregationService.ExtractionStats(), CancellationToken.None);

        pattern.Status.Should().Be(after);
        pattern.PatternContent.Should().Be("Tables public.customers + public.orders are frequently joined together (3 queries)");
    }

    // The error rate and the query count of a documentation gap are its statistics; the table is its substance.
    [TestCase("public.orders", 0.4, 5, McpPatternStatus.AutoApproved)]
    [TestCase("public.order_lines", 0.5, 6, McpPatternStatus.NeedsEvidence)]
    public async Task ADocumentationGap_WhoseRateAndCountAloneMove_StaysAutoApproved(string storedTable, double storedRate, int storedTotal, McpPatternStatus after)
    {
        var pattern = Pattern(McpPatternType.DocumentationGap, DocumentationGapContent(storedTable, storedRate, storedTotal), null, null);
        var signals = Signals("SELECT id FROM orders");
        signals.AddRange(Signals("SELECT id FROM orders"));
        foreach (var signal in signals.Take(3))
        {
            signal.IsSuccessful = false;
        }

        var (service, context) = Build(pattern);

        await service.DetectDocumentationGapsAsync(context, ProjectId, DataSourceId, signals, new McpLearningAggregationService.ExtractionStats(), CancellationToken.None);

        pattern.Status.Should().Be(after);
        pattern.PatternContent.Should().Be(DocumentationGapContent("public.orders", 0.5, 6));
    }

    [TestCase("SELECT  id\n FROM orders -- note", "SELECT id FROM orders")]
    [TestCase("SELECT id FROM orders WHERE status = 'A  B'", "SELECT id FROM orders WHERE status = 'A  B'")]
    [TestCase("SELECT \"My  Id\" FROM orders", "SELECT \"My  Id\" FROM orders")]
    [TestCase("SELECT id /* a */ FROM Orders", "SELECT id FROM Orders")]
    public void TheComparisonForm_CollapsesOnlyTheWhitespaceBetweenTokens(string sql, string expected)
    {
        GroundingSqlText.ForComparison(sql, "PostgreSQL").Should().Be(expected);
    }

    [Test]
    public void TheComparisonForm_OfSqlTheDialectCannotRead_IsNone()
    {
        GroundingSqlText.ForComparison("SELECT id FROM orders", null).Should().BeNull("without a dialect the SQL compares as stored");
    }

    private static string CommonQueryContent(int count)
    {
        return $"Common query pattern for public.orders ({count} similar queries)";
    }

    // As the detector writes it, in the current culture.
    private static string DocumentationGapContent(string table, double rate, int total)
    {
        return string.Format(CultureInfo.CurrentCulture, "Table '{0}' has a {1:P0} error rate across {2} queries. Schema descriptions may be missing or misleading.", table, rate, total);
    }

    private static McpLearnedPattern Pattern(McpPatternType type, string content, string? question, string? sql, string? column = null, string table = "orders")
    {
        return new McpLearnedPattern
        {
            Id = 31,
            ProjectId = ProjectId,
            DataSourceId = DataSourceId,
            SchemaName = "public",
            TableName = table,
            ColumnName = column,
            PatternType = type,
            PatternContent = content,
            ExampleQuestion = question,
            ExampleSql = sql,
            SignalCount = 1,
            Confidence = 0.5,
            Status = McpPatternStatus.AutoApproved
        };
    }

    private static List<McpQuerySignal> Signals(string sql, string tables = "[\"public.orders\"]")
    {
        return Enumerable.Range(0, 3)
            .Select(_ =>
                new McpQuerySignal
                {
                    Tool = "ask",
                    Question = Question,
                    ProjectId = ProjectId,
                    DataSourceId = DataSourceId,
                    GeneratedSql = sql,
                    TablesUsed = tables,
                    IsSuccessful = true
                })
            .ToList();
    }

    private static McpQuerySignal CorrectionSignal()
    {
        return new McpQuerySignal
        {
            Tool = "ask",
            Question = "revenue in dollars",
            ProjectId = ProjectId,
            DataSourceId = DataSourceId,
            GeneratedSql = "SELECT amount FROM orders",
            CorrectedSql = "SELECT total FROM orders",
            RetrySucceeded = true,
            SchemaValidationFailed = true,
            SchemaValidationError = "Column 'amount' does not exist on 'orders'. Available: id, status, total",
            TablesUsed = "[\"public.orders\"]",
            IsSuccessful = true
        };
    }

    private static (McpLearningAggregationService Service, BeaconContext Context) Build(
        McpLearnedPattern existing,
        ILessonExtractor? extractor = null,
        IGroundingSqlCheck? check = null)
    {
        var saved = new List<object>();
        var context = new RecordingBeaconContext(
            new Dictionary<Type, object> { [typeof(McpLearnedPattern)] = RecordingBeaconContext.MemorySet([existing], saved) },
            saved);
        var service = new McpLearningAggregationService(
            Mock.Of<IDbContextFactory<BeaconContext>>(),
            SettingsProviderMock.Create().Object,
            check ?? TestGroundingSqlChecks.Real(),
            NullLogger<McpLearningAggregationService>.Instance,
            lessonExtractor: extractor);

        return (service, context);
    }
}

using System.Text.RegularExpressions;
using Beacon.AI.Services.Learning;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.McpLearning;
using Beacon.Core.Handlers.Metadata;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Grounding;

/// <summary>
/// The conditional writes that keep grounding decisions on the content they were made on, as PostgreSQL and SQL Server
/// receive them, captured without a database: a relationship's edit, removal and verification compare the label,
/// cardinality and verification read; a learned pattern's decision and auto-approval compare the stored content digest —
/// never the text, which a case-insensitive collation (or SQL Server's trailing-space rule) would let a concurrent case-only
/// or trailing-space change pass — and a pattern without a digest has it stored first, only while it holds none and the
/// content read; the aggregation's refresh writes the digest with the content it writes.
/// </summary>
[TestFixture]
public class GroundingConditionalWriteTranslationTests
{
    private const string Content = "Common query pattern for public.orders (3 similar queries)";
    private const string Question = "orders last week";
    private const string Sql = "SELECT id FROM orders";

    private static readonly ProjectAccessScope EditorOfBoth = TestProjectAccess.Scope(
        new Dictionary<int, ProjectRole> { [1] = ProjectRole.Editor, [2] = ProjectRole.Editor },
        includesOpen: false,
        notOpen: []);

    [TestCase(false, "UPDATE beacon.schema_relationships AS s", "s.label = @", "s.cardinality = @", "s.is_verified = @", "s.archived_time IS NULL", "is_verified = @", "verified_by_user_id = NULL", "verified_time = NULL")]
    [TestCase(true, "UPDATE [s]", "[s].[Label] = @", "[s].[Cardinality] = @", "[s].[IsVerified] = @", "[s].[ArchivedTime] IS NULL", "[s].[IsVerified] = @", "[s].[VerifiedByUserId] = NULL", "[s].[VerifiedTime] = NULL")]
    public async Task ARelationshipEdit_IsWrittenWithItsVerification_OnlyWhileItHoldsWhatWasRead_Translates(
        bool sqlServer, string update, string label, string cardinality, string verified, string live, string setVerified, string setVerifier, string setTime)
    {
        var capture = Capture(sqlServer)
            .ThenRow("customer", (int)SchemaRelationshipCardinality.OneToMany, false)
            .ThenRowsAffected(1);
        var handler = new UpdateSchemaRelationshipHandler(capture.Factory(), Mock.Of<ISchemaGraphService>(), TestProjectAccess.Evaluator(EditorOfBoth), TestPermissions.None, TestProjectAccess.Actors(), new RecordingSecurityAudit());

        var result = await handler.Handle(Relabel("buyer"), CancellationToken.None);

        result.IsVerified.Should().BeFalse();
        capture.Commands.Should().HaveCount(2, "the read, then one conditional write");
        capture.Commands[1].Should().Contain(update).And.Contain(label).And.Contain(cardinality).And.Contain(verified).And.Contain(live)
            .And.Contain(setVerified).And.Contain(setVerifier).And.Contain(setTime);
        ComparedWith(capture, 1, sqlServer ? "[s].[Label]" : "s.label").Should().Be("customer", "the label read");
        ComparedWith(capture, 1, sqlServer ? "[s].[IsVerified]" : "s.is_verified").Should().Be(false, "the verification read");
        capture.CommandParameters[1].Values.Should().Contain("buyer", "the label written");
    }

    // A verification between the read and the write: the write changes nothing, and the edit is refused.
    [TestCase(false)]
    [TestCase(true)]
    public async Task ARelationshipEdit_ThatWritesNothing_IsRefused_Translates(bool sqlServer)
    {
        var capture = Capture(sqlServer)
            .ThenRow("customer", (int)SchemaRelationshipCardinality.OneToMany, false)
            .ThenRowsAffected(0);
        var graph = new Mock<ISchemaGraphService>();
        var handler = new UpdateSchemaRelationshipHandler(capture.Factory(), graph.Object, TestProjectAccess.Evaluator(EditorOfBoth), TestPermissions.None, TestProjectAccess.Actors(), new RecordingSecurityAudit());

        var act = () => handler.Handle(Relabel("buyer"), CancellationToken.None);

        (await act.Should().ThrowAsync<BeaconConflictException>()).Which.Message.Should().Be(UpdateSchemaRelationshipHandler.ChangedSinceLoadedMessage);
        graph.Verify(x => x.Invalidate(It.IsAny<int>()), Times.Never);
    }

    [TestCase(false, "SET archived_time = @", "s.label = @", "s.cardinality = @", "s.is_verified = @", "s.archived_time IS NULL")]
    [TestCase(true, "SET [s].[ArchivedTime] = @", "[s].[Label] = @", "[s].[Cardinality] = @", "[s].[IsVerified] = @", "[s].[ArchivedTime] IS NULL")]
    public async Task ARelationshipRemoval_ArchivesItOnlyWhileItHoldsWhatWasRead_Translates(
        bool sqlServer, string archive, string label, string cardinality, string verified, string live)
    {
        var capture = Capture(sqlServer)
            .ThenRow("customer", (int)SchemaRelationshipCardinality.OneToMany, false)
            .ThenRowsAffected(1);
        var handler = new DeleteSchemaRelationshipHandler(
            capture.Factory(), Mock.Of<ISchemaGraphService>(), TestProjectAccess.Evaluator(EditorOfBoth), TestPermissions.None, TestProjectAccess.Actors(), new RecordingSecurityAudit(), NullLogger<DeleteSchemaRelationshipHandler>.Instance);

        await handler.Handle(new DeleteSchemaRelationshipCommand(10, 41), CancellationToken.None);

        capture.Commands.Should().HaveCount(2);
        capture.Commands[1].Should().Contain(archive).And.Contain(label).And.Contain(cardinality).And.Contain(verified).And.Contain(live);
    }

    [TestCase(false, "SET is_verified = @", "s.label = @", "s.cardinality = @", "s.archived_time IS NULL")]
    [TestCase(true, "SET [s].[IsVerified] = @", "[s].[Label] = @", "[s].[Cardinality] = @", "[s].[ArchivedTime] IS NULL")]
    public async Task AVerification_IsWrittenOnlyWhileTheRelationshipHoldsWhatTheCuratorReviewed_Translates(
        bool sqlServer, string verify, string label, string cardinality, string live)
    {
        var capture = Capture(sqlServer)
            .ThenRow("customer", (int)SchemaRelationshipCardinality.OneToMany)
            .ThenRowsAffected(1);
        var handler = new VerifySchemaRelationshipHandler(capture.Factory(), Mock.Of<ISchemaGraphService>(), TestProjectAccess.Actors(), new RecordingSecurityAudit(), NullLogger<VerifySchemaRelationshipHandler>.Instance);

        await handler.Handle(
            new VerifySchemaRelationshipCommand(10, 41, true) { ReviewedLabel = "customer", ReviewedCardinality = SchemaRelationshipCardinality.OneToMany },
            CancellationToken.None);

        capture.Commands.Should().HaveCount(2);
        capture.Commands[1].Should().Contain(verify).And.Contain(label).And.Contain(cardinality).And.Contain(live);
        ComparedWith(capture, 1, sqlServer ? "[s].[Label]" : "s.label").Should().Be("customer", "the label the curator reviewed");
    }

    // A pattern holding its digest: one conditional write, comparing the digest of the content reviewed — never the text.
    [TestCase(false, "UPDATE beacon.mcp_learned_patterns AS m", "m.content_hash = @", "content_hash = @")]
    [TestCase(true, "UPDATE [m]", "[m].[ContentHash] = @", "[m].[ContentHash] = @")]
    public async Task APatternDecision_ComparesTheStoredDigest_Translates(bool sqlServer, string update, string compared, string written)
    {
        var digest = GroundingContentHash.OfPattern(Content, Question, Sql);
        var capture = Capture(sqlServer)
            .ThenRow(2, 7, (int)McpPatternStatus.Pending, Content, Question, Sql, digest)
            .ThenRowsAffected(1);

        await StatusHandler(capture).Handle(Decide(McpPatternStatus.Rejected, digest), CancellationToken.None);

        capture.Commands.Should().HaveCount(2, "the read, then the decision");
        var decision = capture.Commands[1];
        decision.Should().Contain(update).And.Contain(compared).And.Contain(written);
        WhereClauseOf(decision).Should().NotContainAny("pattern_content", "PatternContent", "example_sql", "ExampleSql", "example_question", "ExampleQuestion");
        ComparedWith(capture, 1, compared[..^4]).Should().Be(digest);
    }

    // A pattern stored before Beacon kept the digest: the digest of the content read is stored first — only while the pattern
    // holds no digest and that content — and the decision compares it.
    [TestCase(false, "SET content_hash = @", "m.content_hash IS NULL", "m.pattern_content = @", "m.example_question = @", "m.example_sql = @", "m.content_hash = @")]
    [TestCase(true, "SET [m].[ContentHash] = @", "[m].[ContentHash] IS NULL", "[m].[PatternContent] = @", "[m].[ExampleQuestion] = @", "[m].[ExampleSql] = @", "[m].[ContentHash] = @")]
    public async Task APatternDecision_OnAPatternWithoutADigest_StoresItFirst_Translates(
        bool sqlServer, string store, string withoutDigest, string content, string question, string sql, string compared)
    {
        var digest = GroundingContentHash.OfPattern(Content, Question, Sql);
        var capture = Capture(sqlServer)
            .ThenRow(2, 7, (int)McpPatternStatus.Pending, Content, Question, Sql, DBNull.Value)
            .ThenRowsAffected(1)
            .ThenRowsAffected(1);

        await StatusHandler(capture).Handle(Decide(McpPatternStatus.Rejected, digest), CancellationToken.None);

        capture.Commands.Should().HaveCount(3, "the read, the digest, then the decision");
        capture.Commands[1].Should().Contain(store).And.Contain(withoutDigest).And.Contain(content).And.Contain(question).And.Contain(sql);
        capture.CommandParameters[1].Values.Should().Contain(digest);
        WhereClauseOf(capture.Commands[2]).Should().Contain(compared).And.NotContainAny("pattern_content", "PatternContent");
        ComparedWith(capture, 2, compared[..^4]).Should().Be(digest);
    }

    // The reviewer saw the content read; a concurrent change of its case, or of a trailing space, writes another digest, so
    // the decision's comparison does not match it whatever the collation (the decision is refused).
    [TestCase(false, "SELECT id FROM Orders")]
    [TestCase(true, "SELECT id FROM Orders")]
    [TestCase(false, "SELECT id FROM orders ")]
    [TestCase(true, "SELECT id FROM orders ")]
    public async Task APatternDecision_ComparesADigestThatACaseOrTrailingSpaceChangeDoesNotMatch_Translates(bool sqlServer, string changed)
    {
        var reviewed = GroundingContentHash.OfPattern(Content, Question, Sql);
        var capture = Capture(sqlServer)
            .ThenRow(2, 7, (int)McpPatternStatus.Pending, Content, Question, Sql, reviewed)
            .ThenRowsAffected(0);

        var act = () => StatusHandler(capture).Handle(Decide(McpPatternStatus.Rejected, reviewed), CancellationToken.None);

        (await act.Should().ThrowAsync<BeaconConflictException>()).Which.Message.Should().Be(UpdatePatternStatusHandler.ChangedSinceReviewedMessage);
        ComparedWith(capture, 1, sqlServer ? "[m].[ContentHash]" : "m.content_hash").Should().Be(reviewed)
            .And.BeOfType<string>().Which.Should().MatchRegex("^[0-9a-f]{64}$", "fixed-length lowercase hex compares exactly under any collation");
        GroundingContentHash.OfPattern(Content, Question, changed).Should().NotBeEquivalentTo(reviewed, "the digests differ, ignoring case too");
    }

    // An auto-approval compares the digest of the content the replay measured, from NeedsEvidence, and writes the digest of
    // what it stores.
    [TestCase(false, "m.status = 4", "m.content_hash = @", "SET status = @", "content_hash = @", "last_verified_at = @")]
    [TestCase(true, "[m].[Status] = 4", "[m].[ContentHash] = @", "SET [m].[Status] = @", "[m].[ContentHash] = @", "[m].[LastVerifiedAt] = @")]
    public async Task AnAutoApproval_ComparesTheMeasuredDigest_FromNeedsEvidence_Translates(
        bool sqlServer, string needsEvidence, string compared, string approved, string written, string verifiedAt)
    {
        var digest = GroundingContentHash.OfPattern(Content, Question, Sql);
        var capture = Capture(sqlServer)
            .ThenRow(CandidateRow(digest))
            .ThenRowsAffected(1);

        await Aggregation().PromoteVerifiedCandidatesAsync(await capture.Factory().CreateDbContextAsync(), 1, ReplaySettings(), null, CancellationToken.None);

        capture.Commands.Should().HaveCount(2, "the candidates, then the auto-approval");
        var approval = capture.Commands[1];
        approval.Should().Contain(approved).And.Contain(written).And.Contain(verifiedAt);
        WhereClauseOf(approval).Should().Contain(needsEvidence).And.Contain(compared)
            .And.NotContainAny("pattern_content", "PatternContent", "example_sql", "ExampleSql");
        ComparedWith(capture, 1, compared[..^4]).Should().Be(digest);
    }

    [TestCase(false, "SET content_hash = @", "m.content_hash IS NULL", "m.pattern_content = @", "m.status = 4")]
    [TestCase(true, "SET [m].[ContentHash] = @", "[m].[ContentHash] IS NULL", "[m].[PatternContent] = @", "[m].[Status] = 4")]
    public async Task AnAutoApproval_OfACandidateWithoutADigest_StoresItFirst_Translates(
        bool sqlServer, string store, string withoutDigest, string content, string needsEvidence)
    {
        var capture = Capture(sqlServer)
            .ThenRow(CandidateRow(DBNull.Value))
            .ThenRowsAffected(1)
            .ThenRowsAffected(1);

        await Aggregation().PromoteVerifiedCandidatesAsync(await capture.Factory().CreateDbContextAsync(), 1, ReplaySettings(), null, CancellationToken.None);

        capture.Commands.Should().HaveCount(3, "the candidates, the digest, then the auto-approval");
        capture.Commands[1].Should().Contain(store).And.Contain(withoutDigest).And.Contain(content);
        WhereClauseOf(capture.Commands[2]).Should().Contain(needsEvidence).And.NotContainAny("pattern_content", "PatternContent");
    }

    // The refresh writes the digest of the content it writes, in the same statement.
    [TestCase(false, "content_hash = @", "pattern_content = @")]
    [TestCase(true, "[m].[ContentHash] = @", "[m].[PatternContent] = @")]
    public async Task ARefresh_WritesTheDigestWithTheContent_Translates(bool sqlServer, string digestWritten, string contentWritten)
    {
        var capture = Capture(sqlServer)
            .ThenRow(31, (int)McpPatternStatus.NeedsEvidence, "older content", Question, Sql)
            .ThenRowsAffected(1);

        await Aggregation().DetectCommonQueriesAsync(
            await capture.Factory().CreateDbContextAsync(), 1, 7, Signals(), new McpLearningAggregationService.ExtractionStats(), CancellationToken.None);

        capture.Commands.Should().HaveCount(2, "the pattern, then its refresh");
        capture.Commands[1].Should().Contain(digestWritten).And.Contain(contentWritten);
        capture.CommandParameters[1].Values.Should().Contain(GroundingContentHash.OfPattern(Content, Question, Sql));
    }

    // The value a command's WHERE compares `column` with: the parameter it names there.
    private static object? ComparedWith(SqlCapture capture, int command, string column)
    {
        var match = Regex.Match(WhereClauseOf(capture.Commands[command]), Regex.Escape(column) + @" = (@\w+)");
        match.Success.Should().BeTrue($"the write compares {column} with a parameter");
        var parameters = capture.CommandParameters[command];
        var name = match.Groups[1].Value;

        return parameters.TryGetValue(name, out var value) ? value : parameters[name.TrimStart('@')];
    }

    // The conditions of a write: what follows its WHERE.
    private static string WhereClauseOf(string command)
    {
        var where = command.LastIndexOf("WHERE", StringComparison.Ordinal);

        return where < 0 ? "" : command[where..];
    }

    private static object[] CandidateRow(object contentHash)
    {
        return
        [
            31, DBNull.Value, 0.7, contentHash, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), 7, Question, Sql, DBNull.Value, DBNull.Value,
            Content, (int)McpPatternType.CommonQuery, 1, DBNull.Value, DBNull.Value, "public", 3, (int)McpPatternStatus.NeedsEvidence, DBNull.Value, "orders"
        ];
    }

    private static List<McpQuerySignal> Signals()
    {
        return Enumerable.Range(0, 3)
            .Select(_ =>
                new McpQuerySignal
                {
                    Tool = "ask",
                    Question = Question,
                    ProjectId = 1,
                    DataSourceId = 7,
                    GeneratedSql = Sql,
                    TablesUsed = "[\"public.orders\"]",
                    IsSuccessful = true
                })
            .ToList();
    }

    private static McpLearningAggregationService Aggregation()
    {
        var verifier = new Mock<IPatternReplayVerifier>();
        verifier
            .Setup(x => x.VerifyAsync(It.IsAny<McpLearnedPattern>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReplayVerdict(RelevantCases: 1, BaselineFailing: 1, Flipped: 1, Regressions: 0, Errored: 0, Passed: true));

        return new McpLearningAggregationService(
            Mock.Of<IDbContextFactory<BeaconContext>>(),
            SettingsProviderMock.Create().Object,
            TestGroundingSqlChecks.PassThrough().Object,
            NullLogger<McpLearningAggregationService>.Instance,
            lessonExtractor: null,
            replayVerifier: verifier.Object);
    }

    private static McpSettingsData ReplaySettings()
    {
        return new McpSettingsData
        {
            EnableLearning = true,
            EnableReplayVerification = true,
            LearningReplayMinFlips = 1
        };
    }

    private static UpdatePatternStatusHandler StatusHandler(SqlCapture capture)
    {
        return new UpdatePatternStatusHandler(
            capture.Factory(),
            TestGroundingSqlChecks.PassThrough().Object,
            TestProjectAccess.Actors(),
            new RecordingSecurityAudit(),
            NullLogger<UpdatePatternStatusHandler>.Instance);
    }

    private static UpdatePatternStatusCommand Decide(McpPatternStatus status, string reviewedContentHash)
    {
        return new UpdatePatternStatusCommand
        {
            PatternId = 31,
            NewStatus = status,
            ReviewedContentHash = reviewedContentHash
        };
    }

    private static UpdateSchemaRelationshipCommand Relabel(string label)
    {
        return new UpdateSchemaRelationshipCommand
        {
            DataSourceId = 10,
            Id = 41,
            Label = label,
            Cardinality = SchemaRelationshipCardinality.OneToMany
        };
    }

    private static SqlCapture Capture(bool sqlServer) => sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture();
}

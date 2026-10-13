using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.McpEval;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Retention;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// <see cref="RecordQueryFeedbackHandler"/>: the human correctness feedback loop records a verdict, the user who recorded
/// it and the checked corrected SQL — and nothing else. No verdict creates a golden case (an Admin promotes candidates);
/// corrected SQL passes <see cref="IGroundingSqlCheck"/> in the signal's data source's dialect and is stored without its
/// comments; SQL the check refuses, a signal without a single data source and a locked content-retention decision keep the
/// verdict and drop only the SQL, and the result says so with the reason. A locked <see cref="IContentRetentionPolicy"/>
/// decision drops explicit feedback content. Runs the real handler over in-memory sets (no DB, no forbidden
/// <c>UseInMemoryDatabase</c> — §4.7).
/// </summary>
[TestFixture]
public class RecordQueryFeedbackHandlerTests
{
    private const int ProjectId = 3;
    private const int DataSourceId = TestGroundingSqlChecks.DataSourceId;
    private const int RaterId = 3;

    [Test]
    public async Task ACorrectVerdictWithCorrectedSql_CreatesNoGoldenCase_AndStoresTheCheckedSqlWithoutComments()
    {
        // The asker rates their own answer correct and supplies SQL with a comment in it: the verdict and the comment-free
        // SQL are recorded on the signal, and no golden case exists afterwards — not even an inactive one.
        var signal = NewSignal(1, ProjectId, DataSourceId, generatedSql: "SELECT id FROM orders");
        signal.UserId = RaterId;
        var store = new Store(signal);
        var editor = TestProjectAccess.Scope(new Dictionary<int, ProjectRole> { [ProjectId] = ProjectRole.Editor }, includesOpen: false, notOpen: []);
        var handler = Handler(store, TestGroundingSqlChecks.Real(), access: editor);

        await handler.Handle(
            new RecordQueryFeedbackCommand(1, McpUserVerdict.Correct, "SELECT id, status FROM orders -- a remark for the model"),
            CancellationToken.None);

        signal.UserVerdict.Should().Be(McpUserVerdict.Correct);
        signal.UserCorrectedSql.Should().Be("SELECT id, status FROM orders");
        signal.FeedbackByUserId.Should().Be(RaterId);
        store.Cases.Should().BeEmpty();
        store.Saved.OfType<McpEvalCase>().Should().BeEmpty("a verdict never creates a golden case; an Admin promotes candidates");
    }

    [Test]
    public async Task ACorrectVerdict_OnASignalAlreadyPromoted_ChangesOnlyTheSignal()
    {
        var signal = NewSignal(1, ProjectId, DataSourceId, generatedSql: "SELECT id FROM orders");
        var promoted = new McpEvalCase { Id = 50, ProjectId = ProjectId, DataSourceId = DataSourceId, Question = signal.Question, GoldSql = "SELECT id FROM orders", SourceSignalId = 1 };
        var store = new Store(signal, promoted);
        var check = TestGroundingSqlChecks.PassThrough();

        var result = await Handler(store, check.Object).Handle(
            new RecordQueryFeedbackCommand(1, McpUserVerdict.Correct, CorrectedSql: "SELECT status FROM orders", Note: "human fix"),
            CancellationToken.None);

        result.Should().Be(new RecordQueryFeedbackResult(true, null));
        signal.UserCorrectedSql.Should().Be("SELECT status FROM orders");
        signal.FeedbackNote.Should().Be("human fix");
        store.Cases.Should().ContainSingle().Which.GoldSql.Should().Be("SELECT id FROM orders", "the golden case is an Admin's to edit");
        store.Saved.Should().BeEmpty();
        check.Verify(x => x.CheckAsync(DataSourceId, "SELECT status FROM orders", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Incorrect_RecordsTheVerdictTheNoteAndTheRater()
    {
        var signal = NewSignal(1, ProjectId, DataSourceId, generatedSql: "SELECT id FROM orders");
        var store = new Store(signal);

        var result = await Handler(store, TestGroundingSqlChecks.PassThrough().Object).Handle(
            new RecordQueryFeedbackCommand(1, McpUserVerdict.Incorrect, Note: "wrong join"),
            CancellationToken.None);

        result.Should().Be(new RecordQueryFeedbackResult(false, null), "no corrected SQL was supplied");
        signal.UserVerdict.Should().Be(McpUserVerdict.Incorrect);
        signal.FeedbackNote.Should().Be("wrong join");
        signal.UserCorrectedSql.Should().BeNull();
        signal.FeedbackByUserId.Should().Be(RaterId);
        store.Saved.Should().BeEmpty();
    }

    [Test]
    public async Task UnknownSignal_Throws()
    {
        var store = new Store();

        var act = async () => await Handler(store, TestGroundingSqlChecks.PassThrough().Object).Handle(
            new RecordQueryFeedbackCommand(404, McpUserVerdict.Correct),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
    }

    // Blocked corrected SQL refuses the call (an audited tool error, a 400 on POST /eval/feedback): nothing is stored, the
    // verdict included.
    [Test]
    public async Task CorrectedSqlTheCheckRefuses_KeepsTheVerdictAndTheNote_DropsOnlyTheSql_AndSaysWhy()
    {
        var signal = NewSignal(1, ProjectId, DataSourceId, generatedSql: "SELECT id FROM orders");
        signal.UserCorrectedSql = "SELECT id FROM orders -- an earlier fix";
        var store = new Store(signal);

        var result = await Handler(store, TestGroundingSqlChecks.Real()).Handle(
            new RecordQueryFeedbackCommand(1, McpUserVerdict.Correct, CorrectedSql: "DELETE FROM orders", Note: "human fix"),
            CancellationToken.None);

        result.CorrectedSqlRecorded.Should().BeFalse();
        result.CorrectedSqlNotRecordedReason.Should().StartWith("it did not pass the SQL check: ");
        signal.UserVerdict.Should().Be(McpUserVerdict.Correct, "the verdict stands");
        signal.UserCorrectedSql.Should().BeNull("only the SQL is dropped");
        signal.FeedbackNote.Should().Be("human fix");
        signal.FeedbackByUserId.Should().Be(RaterId);
        store.SaveCount.Should().Be(1);
    }

    [Test]
    public async Task CorrectedSqlWithACommentThatCannotBeRemoved_IsDropped_AndTheReasonNamesLiterals()
    {
        var signal = NewSignal(1, ProjectId, DataSourceId, generatedSql: "SELECT id FROM orders");
        var store = new Store(signal);

        var result = await Handler(store, TestGroundingSqlChecks.Real()).Handle(
            new RecordQueryFeedbackCommand(1, McpUserVerdict.Correct, CorrectedSql: "SELECT id FROM orders /* never closed"),
            CancellationToken.None);

        result.Should().Be(new RecordQueryFeedbackResult(false, $"it did not pass the SQL check: {GroundingSqlCheck.CommentRefusal}"));
        result.CorrectedSqlNotRecordedReason.Should().Contain("string literal");
        signal.UserVerdict.Should().Be(McpUserVerdict.Correct);
        signal.UserCorrectedSql.Should().BeNull();
    }

    // Comment characters inside a string literal are kept with it; a real comment beside them is removed.
    [Test]
    public async Task CorrectedSqlWithCommentCharactersInsideALiteral_IsKept()
    {
        var signal = NewSignal(1, ProjectId, DataSourceId, generatedSql: "SELECT id FROM orders");
        var store = new Store(signal);

        var result = await Handler(store, TestGroundingSqlChecks.Real()).Handle(
            new RecordQueryFeedbackCommand(1, McpUserVerdict.Correct, CorrectedSql: "SELECT id FROM orders WHERE status = 'x--y' -- the fix"),
            CancellationToken.None);

        result.Should().Be(new RecordQueryFeedbackResult(true, null));
        signal.UserCorrectedSql.Should().Be("SELECT id FROM orders WHERE status = 'x--y'");
    }

    // A cross-source or knowledge answer has no single data source whose dialect the SQL could be checked in.
    [Test]
    public async Task ASignalWithoutASingleDataSource_KeepsTheVerdict_AndDropsTheCorrectedSql()
    {
        var signal = NewSignal(1, ProjectId, dataSourceId: null, generatedSql: "SELECT 1");
        var store = new Store(signal);
        var check = TestGroundingSqlChecks.Refusing();

        var result = await Handler(store, check.Object).Handle(
            new RecordQueryFeedbackCommand(1, McpUserVerdict.Correct, CorrectedSql: "SELECT 2", Note: "right, across both sources"),
            CancellationToken.None);

        result.Should().Be(new RecordQueryFeedbackResult(false, RecordQueryFeedbackHandler.NoSingleDataSourceReason));
        signal.UserVerdict.Should().Be(McpUserVerdict.Correct);
        signal.UserCorrectedSql.Should().BeNull();
        signal.FeedbackNote.Should().Be("right, across both sources");
        signal.FeedbackByUserId.Should().Be(RaterId);
        check.VerifyNoOtherCalls();
    }

    [Test]
    public async Task FeedbackContentNotAllowed_DropsCorrectedSqlAndNote_WithoutCheckingIt_ButPersistsVerdict()
    {
        var signal = NewSignal(1, ProjectId, DataSourceId, generatedSql: "SELECT 1");
        var store = new Store(signal);
        var check = TestGroundingSqlChecks.Refusing();

        var result = await Handler(store, check.Object, BuildPolicy(retainQueryContent: true, allowExplicitFeedbackContent: false)).Handle(
            new RecordQueryFeedbackCommand(1, McpUserVerdict.Correct, CorrectedSql: "SELECT 2", Note: "human fix"),
            CancellationToken.None);

        result.Should().Be(new RecordQueryFeedbackResult(false, RecordQueryFeedbackHandler.RetentionOffReason));
        signal.UserVerdict.Should().Be(McpUserVerdict.Correct);
        signal.UserCorrectedSql.Should().BeNull();
        signal.FeedbackNote.Should().BeNull();
        check.VerifyNoOtherCalls();
    }

    [Test]
    public async Task ContentLocked_RecordsTheVerdictOnly()
    {
        var signal = NewSignal(1, ProjectId, DataSourceId, generatedSql: "SELECT 1");
        var store = new Store(signal);

        await Handler(store, TestGroundingSqlChecks.PassThrough().Object, BuildPolicy(retainQueryContent: false, allowExplicitFeedbackContent: false)).Handle(
            new RecordQueryFeedbackCommand(1, McpUserVerdict.Correct, CorrectedSql: "SELECT 2", Note: "human fix"),
            CancellationToken.None);

        signal.UserVerdict.Should().Be(McpUserVerdict.Correct);
        signal.UserCorrectedSql.Should().BeNull();
        signal.FeedbackNote.Should().BeNull();
        store.Saved.Should().BeEmpty();
    }

    [Test]
    public async Task PolicyThrows_FailsClosed_SoTheToolCanStillAudit()
    {
        // Review SF-F002: ResolveAsync had no guard, so a transient settings failure escaped the handler, past
        // FeedbackTool's InvalidOperationException-only catch, and the §1.7 audit row for that call was lost.
        var signal = NewSignal(1, ProjectId, DataSourceId, generatedSql: "SELECT 1");
        var store = new Store(signal);
        var policy = new Mock<IContentRetentionPolicy>();
        policy
            .Setup(x => x.ResolveAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("settings unavailable"));

        var act = () => Handler(store, TestGroundingSqlChecks.PassThrough().Object, policy).Handle(
            new RecordQueryFeedbackCommand(1, McpUserVerdict.Correct, CorrectedSql: "SELECT 2", Note: "human fix"),
            CancellationToken.None);

        await act.Should().NotThrowAsync();
        signal.UserVerdict.Should().Be(McpUserVerdict.Correct, "the verdict is structure and is always recorded");
        signal.UserCorrectedSql.Should().BeNull("fail closed: no content is retained when the decision is unknown");
        signal.FeedbackNote.Should().BeNull();
    }

    // --- who may rate a signal -------------------------------------------------------------------------------

    [TestCase(3, ProjectRole.Editor, true, TestName = "Feedback_OnTheCallersOwnSignal_IsRecorded")]
    [TestCase(4, ProjectRole.Editor, false, TestName = "Feedback_OnAnotherCallersSignal_IsRefusedForAnEditor")]
    [TestCase(4, ProjectRole.ProjectAdmin, true, TestName = "Feedback_OnAnotherCallersSignal_IsRecordedForAProjectAdmin")]
    [TestCase(null, ProjectRole.Editor, false, TestName = "Feedback_OnASignalWithoutACaller_IsRefusedForAnEditor")]
    public async Task Feedback_IsTheSignalsCallersOrAProjectAdmins(int? signalUserId, ProjectRole role, bool recorded)
    {
        var signal = NewSignal(1, ProjectId, DataSourceId, generatedSql: "SELECT 1");
        signal.UserId = signalUserId;
        var store = new Store(signal);
        var scope = TestProjectAccess.Scope(new Dictionary<int, ProjectRole> { [ProjectId] = role }, includesOpen: true, notOpen: []);
        var check = TestGroundingSqlChecks.PassThrough();
        var handler = Handler(store, check.Object, access: scope);

        var act = () => handler.Handle(new RecordQueryFeedbackCommand(1, McpUserVerdict.Incorrect, CorrectedSql: "SELECT 2"), CancellationToken.None);

        if (recorded)
        {
            await act.Should().NotThrowAsync();
            signal.UserVerdict.Should().Be(McpUserVerdict.Incorrect);
            signal.UserCorrectedSql.Should().Be("SELECT 2");
            signal.FeedbackByUserId.Should().Be(RaterId);
        }
        else
        {
            await act.Should().ThrowAsync<BeaconForbiddenException>();
            signal.UserVerdict.Should().Be(McpUserVerdict.Unset, "a refused verdict changes nothing");
            signal.UserCorrectedSql.Should().BeNull();
            signal.FeedbackByUserId.Should().BeNull();
            check.VerifyNoOtherCalls();
        }
    }

    private static RecordQueryFeedbackHandler Handler(
        Store store,
        IGroundingSqlCheck check,
        Mock<IContentRetentionPolicy>? policy = null,
        ProjectAccessScope? access = null)
    {
        return new RecordQueryFeedbackHandler(
            store.Factory,
            check,
            (policy ?? BuildPolicy()).Object,
            TestProjectAccess.Evaluator(access ?? TestProjectAccess.Unrestricted),
            TestProjectAccess.Actors(beaconUserId: RaterId),
            NullLogger<RecordQueryFeedbackHandler>.Instance);
    }

    private static Mock<IContentRetentionPolicy> BuildPolicy(
        bool retainQueryContent = true, bool allowExplicitFeedbackContent = true)
    {
        var policy = new Mock<IContentRetentionPolicy>();
        policy
            .Setup(x => x.ResolveAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentRetentionDecision(retainQueryContent, allowExplicitFeedbackContent));
        return policy;
    }

    private static McpQuerySignal NewSignal(int id, int? projectId, int? dataSourceId, string? generatedSql) =>
        new()
        {
            Id = id,
            ProjectId = projectId,
            DataSourceId = dataSourceId,
            Tool = "ask",
            Question = "What is our total revenue this year?",
            GeneratedSql = generatedSql
        };

    /// <summary>
    /// The signals, golden cases and project links every context the factory hands out serves; whatever a context adds is
    /// recorded in <see cref="Saved"/>, and every save is counted.
    /// </summary>
    private sealed class Store
    {
        public Store(McpQuerySignal? signal = null, McpEvalCase? evalCase = null)
        {
            Signals = signal == null ? [] : [signal];
            Cases = evalCase == null ? [] : [evalCase];
            var links = new List<ProjectDataSource> { new() { ProjectId = ProjectId, DataSourceId = DataSourceId } };
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(
                    new Dictionary<Type, object>
                    {
                        [typeof(McpQuerySignal)] = RecordingBeaconContext.MemorySet(Signals, Saved),
                        [typeof(McpEvalCase)] = RecordingBeaconContext.MemorySet(Cases, Saved),
                        [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(links, Saved)
                    },
                    Saved,
                    onSave: () => SaveCount++));
            Factory = factory.Object;
        }

        public List<McpQuerySignal> Signals { get; }

        public List<McpEvalCase> Cases { get; }

        public List<object> Saved { get; } = [];

        public int SaveCount { get; private set; }

        public IDbContextFactory<BeaconContext> Factory { get; }
    }
}

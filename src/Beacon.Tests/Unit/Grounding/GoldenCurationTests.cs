using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Data.Interceptors;
using Beacon.Core.Handlers.McpEval;
using Beacon.Core.Handlers.McpLearning;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Retention;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Grounding;

/// <summary>
/// How golden cases and approved patterns are curated: a promotion applies to what the Admin reviewed (the candidate's SQL
/// hash, a Correct verdict on an answer to a question), claims its signal atomically (a promotion that loses the claim to
/// a concurrent one answers with that one's case; one whose signal changed meanwhile is refused, 409), stores gold SQL only
/// as the grounding SQL check returns it (without comments) and records who promoted; an edit records who edited; SQL or a
/// question the checks refuse changes nothing; a caller without a Beacon user is refused (403); the golden list returns
/// the attribution and which cases a prompt leaves out; the candidates list holds the unpromoted Correct answers to
/// questions whose content was retained, with their SQL hash; a pattern decision applies to the content the reviewer saw
/// (409 otherwise), checks the example SQL again on approval, and records the caller. Real handlers over in-memory sets,
/// no database (§4.7).
/// </summary>
[TestFixture]
public class GoldenCurationTests
{
    private const int ProjectId = 1;
    private const int OtherProjectId = 2;
    private const int DataSourceId = TestGroundingSqlChecks.DataSourceId;
    private const int AdminUserId = 11;

    [Test]
    public async Task Promote_StoresTheCheckedSqlWithoutComments_ThePromotingUser_AndClaimsTheSignal()
    {
        const string sql = "-- revenue per order\nSELECT id, total FROM orders /* all of them */";
        var store = new Store();
        var signal = Signal(1, McpUserVerdict.Correct, generatedSql: sql);
        store.Signals.Add(signal);
        store.OnSave = () => store.Saved.OfType<McpEvalCase>().Single().Id = 40;

        var result = await PromoteHandler(store).Handle(new PromoteSignalToGoldenCommand(1, Hash(sql), "checked by hand"), CancellationToken.None);

        result.Should().BeEquivalentTo(new { EvalCaseId = 40, AlreadyPromoted = false });
        var golden = store.Saved.OfType<McpEvalCase>().Should().ContainSingle().Subject;
        golden.GoldSql.Should().Be("SELECT id, total FROM orders");
        golden.PromotedByUserId.Should().Be(AdminUserId);
        golden.ModifiedByUserId.Should().BeNull();
        golden.SourceSignalId.Should().Be(1);
        golden.Notes.Should().Be("checked by hand");
        golden.IsActive.Should().BeTrue();
        signal.PromotedAt.Should().NotBeNull("the promotion claims its signal");
        signal.PromotedEvalCaseId.Should().Be(40);
        store.Capture.Timeline.Should().Equal(SqlCapture.TransactionBegun, SqlCapture.TransactionCommitted);
    }

    // The hash compares the text character for character; the hex digits' case does not matter.
    [Test]
    public async Task Promote_TakesTheReviewedHashInEitherCase()
    {
        var store = new Store();
        store.Signals.Add(Signal(1, McpUserVerdict.Correct, generatedSql: "SELECT id FROM orders"));

        var result = await PromoteHandler(store).Handle(new PromoteSignalToGoldenCommand(1, Hash("SELECT id FROM orders").ToUpperInvariant()), CancellationToken.None);

        result.AlreadyPromoted.Should().BeFalse();
        store.Saved.OfType<McpEvalCase>().Should().ContainSingle();
    }

    // The SQL a promotion starts from is the candidate list's: the rater's corrected SQL, else the signal's corrected SQL,
    // else its generated SQL — and the reviewed hash is that SQL's.
    [Test]
    public async Task Promote_StartsFromTheSqlTheCandidateListShows()
    {
        var store = new Store();
        var signal = Signal(1, McpUserVerdict.Correct, generatedSql: "SELECT id FROM orders");
        signal.CorrectedSql = "SELECT id, total FROM orders";
        signal.UserCorrectedSql = "SELECT status FROM orders";
        store.Signals.Add(signal);

        await PromoteHandler(store).Handle(new PromoteSignalToGoldenCommand(1, Hash("SELECT status FROM orders")), CancellationToken.None);

        store.Saved.OfType<McpEvalCase>().Should().ContainSingle().Which.GoldSql.Should().Be("SELECT status FROM orders");
    }

    // Whatever SQL a signal holds, the candidate list shows the SQL and hash a promotion starts from: promoting with the
    // list's hash stores the list's SQL.
    [Test]
    public async Task Promote_StartsFromTheCandidateListsSql_ForEverySourceOfSql()
    {
        var store = new Store();
        store.Signals.Add(Signal(1, McpUserVerdict.Correct, generatedSql: "SELECT id FROM orders"));
        var corrected = Signal(2, McpUserVerdict.Correct, generatedSql: "SELECT id FROM orders");
        corrected.CorrectedSql = "SELECT id, total FROM orders";
        store.Signals.Add(corrected);
        var rated = Signal(3, McpUserVerdict.Correct, generatedSql: "SELECT id FROM orders");
        rated.CorrectedSql = "SELECT id, total FROM orders";
        rated.UserCorrectedSql = "SELECT status FROM orders";
        store.Signals.Add(rated);
        var candidates = await new GetGoldenCandidatesHandler(store.Factory).Handle(new GetGoldenCandidatesQuery { ProjectId = ProjectId }, CancellationToken.None);

        foreach (var candidate in candidates.Items)
        {
            await PromoteHandler(store).Handle(new PromoteSignalToGoldenCommand(candidate.SignalId, candidate.SqlHash), CancellationToken.None);
        }

        candidates.Items.ToDictionary(x => x.SignalId, x => x.Sql).Should().BeEquivalentTo(new Dictionary<int, string>
        {
            [1] = "SELECT id FROM orders",
            [2] = "SELECT id, total FROM orders",
            [3] = "SELECT status FROM orders"
        });
        store.Saved.OfType<McpEvalCase>().ToDictionary(x => x.SourceSignalId!.Value, x => x.GoldSql)
            .Should().BeEquivalentTo(candidates.Items.ToDictionary(x => x.SignalId, x => x.Sql));
    }

    // What a promotion needs from its signal, each refused (§9.8) before anything is checked or stored.
    [Test]
    public async Task Promote_OfAMissingSignal_IsRefused()
    {
        var store = new Store();

        var act = () => PromoteHandler(store).Handle(new PromoteSignalToGoldenCommand(99, Hash("SELECT id FROM orders")), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Query signal 99 not found.");
        store.Saved.Should().BeEmpty();
    }

    [Test]
    public async Task Promote_OfASignalWithoutSql_IsRefused()
    {
        var store = new Store();
        store.Signals.Add(Signal(1, McpUserVerdict.Correct, generatedSql: null));
        var check = TestGroundingSqlChecks.PassThrough();

        var act = () => PromoteHandler(store, check.Object).Handle(new PromoteSignalToGoldenCommand(1, Hash("")), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Query signal 1 has no SQL to promote.");
        check.VerifyNoOtherCalls();
        store.Saved.Should().BeEmpty();
    }

    [TestCase(true, false, "Query signal 1 is not associated with a project.")]
    [TestCase(false, true, "Query signal 1 is not associated with a data source.")]
    public async Task Promote_OfASignalWithoutAProjectOrADataSource_IsRefused(bool withoutProject, bool withoutDataSource, string message)
    {
        var store = new Store();
        var signal = Signal(1, McpUserVerdict.Correct, generatedSql: "SELECT id FROM orders");
        signal.ProjectId = withoutProject ? null : ProjectId;
        signal.DataSourceId = withoutDataSource ? null : DataSourceId;
        store.Signals.Add(signal);
        var check = TestGroundingSqlChecks.PassThrough();

        var act = () => PromoteHandler(store, check.Object).Handle(new PromoteSignalToGoldenCommand(1, Hash("SELECT id FROM orders")), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
        check.VerifyNoOtherCalls();
        store.Saved.Should().BeEmpty();
        signal.PromotedAt.Should().BeNull();
    }

    // A golden case grounds its project's prompts with its data source's SQL: a data source the project does not link never
    // becomes another project's example (§1.12).
    [Test]
    public async Task Promote_OfASignalWhoseDataSourceItsProjectDoesNotLink_IsRefused()
    {
        var store = new Store();
        var signal = Signal(1, McpUserVerdict.Correct, generatedSql: "SELECT id FROM orders");
        signal.ProjectId = OtherProjectId;
        store.Signals.Add(signal);
        var check = TestGroundingSqlChecks.PassThrough();

        var act = () => PromoteHandler(store, check.Object).Handle(new PromoteSignalToGoldenCommand(1, Hash("SELECT id FROM orders")), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"Data source {DataSourceId} is not linked to project {OtherProjectId}.");
        check.VerifyNoOtherCalls();
        store.Saved.Should().BeEmpty();
        signal.PromotedAt.Should().BeNull();
    }

    // A promotion's notes are content: the retention belt keeps them only in a project that retains query content.
    [TestCase(true, "checked against the finance report")]
    [TestCase(false, null)]
    public async Task Promote_KeepsItsNotes_OnlyWhereTheProjectRetainsContent(bool retainsContent, string? storedNotes)
    {
        var store = new Store();
        store.Signals.Add(Signal(1, McpUserVerdict.Correct, generatedSql: "SELECT id FROM orders"));
        await PromoteHandler(store).Handle(new PromoteSignalToGoldenCommand(1, Hash("SELECT id FROM orders"), "checked against the finance report"), CancellationToken.None);
        var golden = store.Saved.OfType<McpEvalCase>().Should().ContainSingle().Subject;
        using var context = NpgsqlTestContext.Create();
        context.Add(golden);
        int? resolvedProject = null;

        await new ContentRetentionInterceptor(Mock.Of<IServiceProvider>(), NullLogger<ContentRetentionInterceptor>.Instance).RedactTrackedEntriesAsync(
            context.ChangeTracker,
            (x, _) =>
            {
                resolvedProject = x;

                return Task.FromResult(new ContentRetentionDecision(retainsContent, retainsContent));
            });

        golden.Notes.Should().Be(storedNotes);
        resolvedProject.Should().Be(ProjectId, "the case's project decides");
    }

    [TestCase("SELECT missing_column FROM orders")]
    [TestCase("SELECT id FROM orders WHERE status = 'a\u202Eb'")]
    public async Task Promote_OfSqlTheCheckRefuses_CreatesNothing_AndLeavesTheSignalUnclaimed(string sql)
    {
        var store = new Store();
        var signal = Signal(1, McpUserVerdict.Correct, generatedSql: sql);
        store.Signals.Add(signal);

        var act = () => PromoteHandler(store).Handle(new PromoteSignalToGoldenCommand(1, Hash(sql)), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Query signal 1 cannot become a golden case: *");
        store.Saved.Should().BeEmpty();
        store.SaveCount.Should().Be(0);
        signal.PromotedAt.Should().BeNull();
    }

    [Test]
    public async Task Promote_OfSqlOtherThanTheReviewedSql_IsAConflict_AndChecksNothing()
    {
        var store = new Store();
        var signal = Signal(1, McpUserVerdict.Correct, generatedSql: "SELECT id FROM orders");
        signal.UserCorrectedSql = "SELECT status FROM orders";
        store.Signals.Add(signal);
        var check = TestGroundingSqlChecks.PassThrough();

        var act = () => PromoteHandler(store, check.Object).Handle(new PromoteSignalToGoldenCommand(1, Hash("SELECT id FROM orders")), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(PromoteSignalToGoldenHandler.ChangedSinceReviewedMessage);
        check.VerifyNoOtherCalls();
        store.Saved.Should().BeEmpty();
        signal.PromotedAt.Should().BeNull();
    }

    [TestCase(McpUserVerdict.Incorrect)]
    [TestCase(McpUserVerdict.Unset)]
    public async Task Promote_OfASignalNoLongerRatedCorrect_IsAConflict(McpUserVerdict verdict)
    {
        var store = new Store();
        store.Signals.Add(Signal(1, verdict, generatedSql: "SELECT id FROM orders"));

        var act = () => PromoteHandler(store).Handle(new PromoteSignalToGoldenCommand(1, Hash("SELECT id FROM orders")), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(PromoteSignalToGoldenHandler.ChangedSinceReviewedMessage);
        store.Saved.Should().BeEmpty();
    }

    // A query or dry_run signal's "question" is the SQL it ran: only an answer to a question becomes a golden case.
    [TestCase("query")]
    [TestCase("dry_run")]
    public async Task Promote_OfASignalThatAnswersNoQuestion_IsRefused(string tool)
    {
        var store = new Store();
        var signal = Signal(1, McpUserVerdict.Correct, generatedSql: "SELECT id FROM orders");
        signal.Tool = tool;
        store.Signals.Add(signal);

        var act = () => PromoteHandler(store).Handle(new PromoteSignalToGoldenCommand(1, Hash("SELECT id FROM orders")), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Query signal 1 is not an answer to a question*");
        store.Saved.Should().BeEmpty();
    }

    [TestCase("How many\u200B orders?")]
    [TestCase("How many orders?\U000E0041")]
    [TestCase("How many orders\uFE0F?")]
    public async Task Promote_OfAQuestionHoldingCharactersThatDoNotShow_IsRefused(string question)
    {
        var store = new Store();
        var signal = Signal(1, McpUserVerdict.Correct, generatedSql: "SELECT id FROM orders");
        signal.Question = question;
        store.Signals.Add(signal);

        var act = () => PromoteHandler(store).Handle(new PromoteSignalToGoldenCommand(1, Hash("SELECT id FROM orders")), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*question holds characters that do not show on screen*");
        store.Saved.Should().BeEmpty();
    }

    [Test]
    public async Task Promote_ByACallerWithoutABeaconUser_IsRefused_AndReadsNothing()
    {
        var store = new Store();
        store.Signals.Add(Signal(1, McpUserVerdict.Correct, generatedSql: "SELECT id FROM orders"));
        var handler = new PromoteSignalToGoldenHandler(store.Factory, TestGroundingSqlChecks.Real(), NoBeaconUser(), NullLogger<PromoteSignalToGoldenHandler>.Instance);

        var act = () => handler.Handle(new PromoteSignalToGoldenCommand(1, Hash("SELECT id FROM orders")), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(PromoteSignalToGoldenHandler.UnidentifiedCuratorMessage);
        store.ContextsCreated.Should().Be(0);
    }

    [Test]
    public async Task Edit_StoresTheCheckedGoldSqlWithoutComments_AndRecordsTheEditor()
    {
        var store = new Store();
        var golden = Case(5, promotedBy: 4);
        store.Cases.Add(golden);

        await EditHandler(store).Handle(
            new UpdateGoldenCaseCommand { Id = 5, GoldSql = "SELECT status FROM orders -- the better answer", Notes = "edited" },
            CancellationToken.None);

        golden.GoldSql.Should().Be("SELECT status FROM orders");
        golden.GoldResultFingerprint.Should().BeNull();
        golden.Notes.Should().Be("edited");
        golden.ModifiedByUserId.Should().Be(AdminUserId);
        golden.PromotedByUserId.Should().Be(4, "an edit keeps who promoted the case");
        store.SaveCount.Should().Be(1);
    }

    [Test]
    public async Task Edit_OfGoldSqlTheCheckRefuses_ChangesNothing()
    {
        var store = new Store();
        var golden = Case(5, promotedBy: 4);
        store.Cases.Add(golden);

        var act = () => EditHandler(store).Handle(
            new UpdateGoldenCaseCommand { Id = 5, Question = "a new question", GoldSql = "UPDATE orders SET status = 'x'" },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("The gold SQL was not saved: *");
        golden.Question.Should().Be("How many orders?");
        golden.GoldSql.Should().Be("SELECT id FROM orders");
        golden.GoldResultFingerprint.Should().Be("fingerprint");
        golden.ModifiedByUserId.Should().BeNull();
        store.SaveCount.Should().Be(0);
    }

    [Test]
    public async Task Edit_OfAQuestionHoldingCharactersThatDoNotShow_ChangesNothing()
    {
        var store = new Store();
        var golden = Case(5, promotedBy: 4);
        store.Cases.Add(golden);

        var act = () => EditHandler(store).Handle(
            new UpdateGoldenCaseCommand { Id = 5, Question = "How many \u202Eorders?", Notes = "edited" },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("The question was not saved: *");
        golden.Question.Should().Be("How many orders?");
        golden.Notes.Should().BeNull();
        store.SaveCount.Should().Be(0);
    }

    [Test]
    public async Task Edit_ByACallerWithoutABeaconUser_IsRefused_AndChangesNothing()
    {
        var store = new Store();
        var golden = Case(5, promotedBy: 4);
        store.Cases.Add(golden);
        var handler = new UpdateGoldenCaseHandler(store.Factory, TestGroundingSqlChecks.Real(), NoBeaconUser(), NullLogger<UpdateGoldenCaseHandler>.Instance);

        var act = () => handler.Handle(new UpdateGoldenCaseCommand { Id = 5, IsActive = false }, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(UpdateGoldenCaseHandler.UnidentifiedEditorMessage);
        golden.IsActive.Should().BeTrue();
        store.SaveCount.Should().Be(0);
    }

    [Test]
    public async Task Edit_WithoutSql_RecordsTheEditor_AndChecksNothing()
    {
        var store = new Store();
        var golden = Case(5, promotedBy: null);
        store.Cases.Add(golden);
        var check = TestGroundingSqlChecks.Refusing();

        await new UpdateGoldenCaseHandler(store.Factory, check.Object, TestProjectAccess.Actors(beaconUserId: AdminUserId), NullLogger<UpdateGoldenCaseHandler>.Instance)
            .Handle(new UpdateGoldenCaseCommand { Id = 5, IsActive = false }, CancellationToken.None);

        golden.IsActive.Should().BeFalse();
        golden.ModifiedByUserId.Should().Be(AdminUserId);
        check.VerifyNoOtherCalls();
    }

    [Test]
    public async Task TheGoldenList_ReturnsWhoPromotedAndWhoEdited_NullForOlderCases()
    {
        var store = new Store();
        var promoted = Case(5, promotedBy: 4);
        promoted.ModifiedByUserId = AdminUserId;
        store.Cases.Add(promoted);
        store.Cases.Add(Case(6, promotedBy: null));
        store.Users.Add(new BeaconUser { Id = 4, ExternalId = "ext-4", UserName = "ada", DisplayName = "Ada Lovelace" });
        store.Users.Add(new BeaconUser { Id = AdminUserId, ExternalId = "ext-11", UserName = "admin" });

        var items = await new GetGoldenCasesHandler(store.Factory).Handle(new GetGoldenCasesQuery { ProjectId = ProjectId }, CancellationToken.None);

        items.Should().BeEquivalentTo(
            new[]
            {
                new { Id = 5, PromotedByUserId = (int?)4, PromotedByName = (string?)"Ada Lovelace", ModifiedByUserId = (int?)AdminUserId, ModifiedByName = (string?)$"User {AdminUserId}" },
                new { Id = 6, PromotedByUserId = (int?)null, PromotedByName = (string?)null, ModifiedByUserId = (int?)null, ModifiedByName = (string?)null }
            },
            x => x.ExcludingMissingMembers());
    }

    // What a prompt leaves out, read in the case's data source's dialect: a comment that cannot be removed, characters that
    // do not show. Comment characters inside a string literal show, so the case is kept.
    [TestCase("SELECT id FROM orders -- the ids", "How many orders?", false, false)]
    [TestCase("SELECT id FROM orders WHERE status = 'a--b'", "How many orders?", false, false)]
    [TestCase("SELECT id /* unclosed FROM orders", "How many orders?", true, false)]
    [TestCase("SELECT id FROM orders\u200B", "How many orders?", true, false)]
    [TestCase("SELECT id FROM orders", "How many \u2066orders?", false, true)]
    public async Task TheGoldenList_SaysWhichCasesAPromptLeavesOut(string goldSql, string question, bool sqlWithheld, bool questionWithheld)
    {
        var store = new Store();
        var golden = Case(5, promotedBy: 4);
        golden.GoldSql = goldSql;
        golden.Question = question;
        store.Cases.Add(golden);

        var items = await new GetGoldenCasesHandler(store.Factory).Handle(new GetGoldenCasesQuery { ProjectId = ProjectId }, CancellationToken.None);

        items.Should().ContainSingle().Which.Should().BeEquivalentTo(new { GoldSqlWithheld = sqlWithheld, QuestionWithheld = questionWithheld }, x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task TheCandidates_AreUnpromotedCorrectAnswersToQuestions_WithRetainedContent_InTheProject()
    {
        var store = new Store();
        var candidate = Signal(1, McpUserVerdict.Correct, generatedSql: "SELECT id FROM orders");
        candidate.UserId = 5;
        candidate.FeedbackByUserId = 6;
        candidate.FeedbackNote = "matches the finance report";
        store.Signals.Add(candidate);
        store.Signals.Add(Signal(2, McpUserVerdict.Correct, generatedSql: "SELECT id FROM orders"));
        store.Cases.Add(new McpEvalCase { Id = 9, ProjectId = ProjectId, DataSourceId = DataSourceId, Question = "q", GoldSql = "SELECT id FROM orders", SourceSignalId = 2 });
        var otherProject = Signal(3, McpUserVerdict.Correct, generatedSql: "SELECT id FROM orders");
        otherProject.ProjectId = OtherProjectId;
        store.Signals.Add(otherProject);
        store.Signals.Add(Signal(4, McpUserVerdict.Incorrect, generatedSql: "SELECT id FROM orders"));
        store.Signals.Add(Signal(5, McpUserVerdict.Unset, generatedSql: "SELECT id FROM orders"));
        var locked = Signal(6, McpUserVerdict.Correct, generatedSql: null);
        locked.Question = string.Empty;
        store.Signals.Add(locked);
        var crossSource = Signal(7, McpUserVerdict.Correct, generatedSql: "SELECT 1");
        crossSource.DataSourceId = null;
        store.Signals.Add(crossSource);
        var corrected = Signal(8, McpUserVerdict.Correct, generatedSql: "SELECT id FROM orders");
        corrected.ProjectId = OtherProjectId;
        corrected.CorrectedSql = "SELECT id, status FROM orders";
        corrected.UserCorrectedSql = "SELECT status FROM orders";
        store.Signals.Add(corrected);
        var claimed = Signal(10, McpUserVerdict.Correct, generatedSql: "SELECT id FROM orders");
        claimed.PromotedAt = DateTime.UtcNow;
        store.Signals.Add(claimed);
        var direct = Signal(11, McpUserVerdict.Correct, generatedSql: "SELECT id FROM orders");
        direct.Tool = "query";
        direct.Question = "SELECT id FROM orders";
        store.Signals.Add(direct);
        store.Users.Add(new BeaconUser { Id = 5, ExternalId = "ext-5", UserName = "ada", DisplayName = "Ada Lovelace" });
        store.Users.Add(new BeaconUser { Id = 6, ExternalId = "ext-6", UserName = "ben" });
        var handler = new GetGoldenCandidatesHandler(store.Factory);

        var inProject = await handler.Handle(new GetGoldenCandidatesQuery { ProjectId = ProjectId }, CancellationToken.None);
        var all = await handler.Handle(new GetGoldenCandidatesQuery(), CancellationToken.None);

        inProject.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(new GoldenCandidateItem
        {
            SignalId = 1,
            ProjectId = ProjectId,
            DataSourceId = DataSourceId,
            Question = candidate.Question,
            Sql = "SELECT id FROM orders",
            IsUserCorrected = false,
            FeedbackNote = "matches the finance report",
            AskedByUserId = 5,
            AskedByName = "Ada Lovelace",
            RatedByUserId = 6,
            RatedByName = "User 6",
            CreatedTime = candidate.CreatedTime
        });
        inProject.Items[0].SqlHash.Should().Be(Hash("SELECT id FROM orders")).And.MatchRegex("^[0-9a-f]{64}$");
        all.Items.Select(x => x.SignalId).Should().BeEquivalentTo(
            [1, 3, 8],
            "promoted, claimed, unrated, incorrect, content-locked, cross-source and query signals are no candidates");
        all.Items.Single(x => x.SignalId == 8).Should().BeEquivalentTo(
            new { Sql = "SELECT status FROM orders", IsUserCorrected = true, SqlHash = Hash("SELECT status FROM orders") },
            x => x.ExcludingMissingMembers());
    }

    // The pattern list says which examples a prompt leaves out (read in the pattern's data source's dialect), and hands out
    // the hash of what the reviewer sees, which a decision sends back.
    [TestCase("SELECT id FROM orders -- the ids", "orders last week", false, false)]
    [TestCase("SELECT id FROM orders WHERE status = 'a--b'", "orders last week", false, false)]
    [TestCase("SELECT id /* never closed FROM orders", "orders last week", true, false)]
    [TestCase("SELECT id FROM orders\u200B", "orders last week", true, false)]
    [TestCase("SELECT id FROM orders", "orders \u202Elast week", false, true)]
    public async Task ThePatternList_SaysWhichExamplesAPromptLeavesOut_AndHashesWhatTheReviewerSees(
        string exampleSql, string exampleQuestion, bool sqlWithheld, bool questionWithheld)
    {
        var pattern = Pattern(exampleSql);
        pattern.ExampleQuestion = exampleQuestion;
        var store = new Store(pattern);

        var page = await new GetLearnedPatternsHandler(store.Factory, TestProjectAccess.Evaluator(TestProjectAccess.Unrestricted))
            .Handle(new GetLearnedPatternsQuery(), CancellationToken.None);

        page.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { ExampleSqlWithheld = sqlWithheld, ExampleQuestionWithheld = questionWithheld, ContentHash = HashOf(pattern) },
            x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task ApprovingAPattern_ChecksItsExampleSqlAgain_AndStoresItWithoutComments()
    {
        var pattern = Pattern("SELECT id FROM orders /* the usual */");
        var store = new Store(pattern);

        await StatusHandler(store, TestGroundingSqlChecks.Real())
            .Handle(new UpdatePatternStatusCommand { PatternId = pattern.Id, NewStatus = McpPatternStatus.Approved, ReviewedContentHash = HashOf(pattern) }, CancellationToken.None);

        pattern.Status.Should().Be(McpPatternStatus.Approved);
        pattern.ExampleSql.Should().Be("SELECT id FROM orders");
        pattern.ReviewedByUserId.Should().Be(AdminUserId, "the reviewer is the current caller");
        pattern.ReviewedAt.Should().NotBeNull();
    }

    [Test]
    public async Task ApprovingAPattern_WhoseExampleSqlTheCheckRefuses_ChangesNothing()
    {
        var pattern = Pattern("SELECT missing_column FROM orders");
        var store = new Store(pattern);

        var act = () => StatusHandler(store, TestGroundingSqlChecks.Real())
            .Handle(new UpdatePatternStatusCommand { PatternId = pattern.Id, NewStatus = McpPatternStatus.Approved, ReviewedContentHash = HashOf(pattern) }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"Pattern {pattern.Id} was not approved: *");
        pattern.Status.Should().Be(McpPatternStatus.NeedsEvidence);
        pattern.ExampleSql.Should().Be("SELECT missing_column FROM orders");
        pattern.ReviewedByUserId.Should().BeNull();
        store.SaveCount.Should().Be(0);
    }

    // A lesson without example SQL has no SQL to check: the approval is the reviewer's alone.
    [TestCase(null)]
    [TestCase("  ")]
    public async Task ApprovingAPatternWithoutExampleSql_ChecksNothing_AndRecordsTheReviewer(string? exampleSql)
    {
        var pattern = Pattern(exampleSql!);
        var store = new Store(pattern);
        var reads = 0;

        await StatusHandler(store, TestGroundingSqlChecks.Real(onRead: () => reads++))
            .Handle(new UpdatePatternStatusCommand { PatternId = pattern.Id, NewStatus = McpPatternStatus.Approved, ReviewedContentHash = HashOf(pattern) }, CancellationToken.None);

        pattern.Should().BeEquivalentTo(
            new { Status = McpPatternStatus.Approved, ExampleSql = exampleSql, ReviewedByUserId = (int?)AdminUserId },
            x => x.ExcludingMissingMembers());
        pattern.ReviewedAt.Should().NotBeNull();
        reads.Should().Be(0, "no SQL is checked, so no data source or catalog is read");
    }

    [TestCase(McpPatternStatus.Rejected)]
    [TestCase(McpPatternStatus.Pending)]
    public async Task RejectingAPattern_OrSettingItBackToPending_ChecksNothing_AndRecordsTheReviewer(McpPatternStatus status)
    {
        var pattern = Pattern("SELECT missing_column FROM orders");
        var store = new Store(pattern);
        var check = TestGroundingSqlChecks.Refusing();

        await StatusHandler(store, check.Object)
            .Handle(new UpdatePatternStatusCommand { PatternId = pattern.Id, NewStatus = status, ReviewedContentHash = HashOf(pattern) }, CancellationToken.None);

        pattern.Status.Should().Be(status);
        pattern.ReviewedByUserId.Should().Be(AdminUserId);
        check.VerifyNoOtherCalls();
    }

    // A decision applies to the content the reviewer saw: content that differs from the reviewed hash is not decided.
    [TestCase(McpPatternStatus.Approved)]
    [TestCase(McpPatternStatus.Rejected)]
    public async Task ADecisionOnContentOtherThanTheReviewedContent_IsAConflict_AndChangesNothing(McpPatternStatus status)
    {
        var pattern = Pattern("SELECT id FROM orders");
        var reviewed = HashOf(pattern);
        pattern.PatternContent = "Common query pattern for public.orders, refreshed";
        var store = new Store(pattern);
        var check = TestGroundingSqlChecks.PassThrough();

        var act = () => StatusHandler(store, check.Object)
            .Handle(new UpdatePatternStatusCommand { PatternId = pattern.Id, NewStatus = status, ReviewedContentHash = reviewed }, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(UpdatePatternStatusHandler.ChangedSinceReviewedMessage);
        pattern.Status.Should().Be(McpPatternStatus.NeedsEvidence);
        pattern.ReviewedByUserId.Should().BeNull();
        check.VerifyNoOtherCalls();
    }

    // A refresh that lands between the decision's read and its write is never decided unseen: the write is conditional on
    // the content read.
    [Test]
    public async Task ARefreshBetweenTheReadAndTheWrite_IsAConflict_AndTheDecisionIsNotWritten()
    {
        var pattern = Pattern("SELECT id FROM orders");
        var store = new Store(pattern);
        var check = new Mock<IGroundingSqlCheck>();
        check
            .Setup(x => x.CheckAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => pattern.ExampleSql = "SELECT status FROM orders")
            .ReturnsAsync((int _, string sql, CancellationToken _) => GroundingSqlCheckResult.Allowed(sql));

        var act = () => StatusHandler(store, check.Object)
            .Handle(new UpdatePatternStatusCommand { PatternId = pattern.Id, NewStatus = McpPatternStatus.Approved, ReviewedContentHash = HashOf(pattern) }, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(UpdatePatternStatusHandler.ChangedSinceReviewedMessage);
        pattern.Status.Should().Be(McpPatternStatus.NeedsEvidence);
        pattern.ReviewedByUserId.Should().BeNull();
    }

    [Test]
    public async Task ADecisionByACallerWithoutABeaconUser_IsRefused_AndChangesNothing()
    {
        var pattern = Pattern("SELECT id FROM orders");
        var store = new Store(pattern);
        var handler = new UpdatePatternStatusHandler(store.Factory, TestGroundingSqlChecks.PassThrough().Object, NoBeaconUser(), NullLogger<UpdatePatternStatusHandler>.Instance);

        var act = () => handler.Handle(
            new UpdatePatternStatusCommand { PatternId = pattern.Id, NewStatus = McpPatternStatus.Rejected, ReviewedContentHash = HashOf(pattern) },
            CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(UpdatePatternStatusHandler.UnidentifiedReviewerMessage);
        pattern.Status.Should().Be(McpPatternStatus.NeedsEvidence);
    }

    // AutoApproved and NeedsEvidence are the aggregation job's; any other value is no status at all.
    [TestCase(McpPatternStatus.AutoApproved)]
    [TestCase(McpPatternStatus.NeedsEvidence)]
    [TestCase((McpPatternStatus)99)]
    public async Task AStatusAReviewerMayNotSet_IsRefused_AndNothingIsRead(McpPatternStatus status)
    {
        var pattern = Pattern("SELECT id FROM orders");
        var store = new Store(pattern);
        var check = TestGroundingSqlChecks.PassThrough();

        var act = () => StatusHandler(store, check.Object)
            .Handle(new UpdatePatternStatusCommand { PatternId = pattern.Id, NewStatus = status, ReviewedContentHash = HashOf(pattern) }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("A pattern can only be approved, rejected or set back to pending.");
        pattern.Status.Should().Be(McpPatternStatus.NeedsEvidence);
        pattern.ReviewedByUserId.Should().BeNull();
        store.SaveCount.Should().Be(0);
        store.ContextsCreated.Should().Be(0);
        check.VerifyNoOtherCalls();
    }

    // A signal promoted before Beacon recorded promotions: the first case promoted from it answers.
    [Test]
    public async Task PromotingASignalPromotedBefore_AnswersWithTheFirstCasePromotedFromIt_AndCreatesNothing()
    {
        var store = new Store();
        store.Signals.Add(Signal(1, McpUserVerdict.Correct, generatedSql: "SELECT id FROM orders"));
        store.Cases.Add(PromotedCase(7, isActive: true));
        store.Cases.Add(PromotedCase(5, isActive: false));

        var result = await PromoteHandler(store).Handle(new PromoteSignalToGoldenCommand(1, Hash("SELECT id FROM orders")), CancellationToken.None);

        result.Should().BeEquivalentTo(new { EvalCaseId = 5, AlreadyPromoted = true });
        store.Saved.Should().BeEmpty();
        store.SaveCount.Should().Be(0);
    }

    // A claimed signal answers with its case, whatever was reviewed: a second promotion is no change.
    [Test]
    public async Task PromotingAClaimedSignal_AnswersWithItsCase_AndChecksNothing()
    {
        var store = new Store();
        var signal = Signal(1, McpUserVerdict.Correct, generatedSql: "SELECT id FROM orders");
        signal.PromotedAt = DateTime.UtcNow;
        signal.PromotedEvalCaseId = 12;
        store.Signals.Add(signal);
        var check = TestGroundingSqlChecks.PassThrough();

        var result = await PromoteHandler(store, check.Object).Handle(new PromoteSignalToGoldenCommand(1, "stale"), CancellationToken.None);

        result.Should().BeEquivalentTo(new { EvalCaseId = 12, AlreadyPromoted = true });
        check.VerifyNoOtherCalls();
        store.Saved.Should().BeEmpty();
    }

    // Two promotions of one signal: the one that claims it first creates the case; the other claims nothing, reads the
    // signal again and answers with the winner's case, creating nothing.
    [Test]
    public async Task APromotionThatLosesTheClaim_AnswersWithTheWinningCase_AndCreatesNothing()
    {
        var store = new Store();
        var signal = Signal(1, McpUserVerdict.Correct, generatedSql: "SELECT id FROM orders");
        store.Signals.Add(signal);
        var check = WinnerCommitsDuringTheCheck(() =>
        {
            signal.PromotedAt = DateTime.UtcNow;
            signal.PromotedEvalCaseId = 9;
            store.Cases.Add(PromotedCase(9, isActive: true));
        });

        var result = await PromoteHandler(store, check).Handle(new PromoteSignalToGoldenCommand(1, Hash("SELECT id FROM orders")), CancellationToken.None);

        result.Should().BeEquivalentTo(new { EvalCaseId = 9, AlreadyPromoted = true });
        store.Saved.Should().BeEmpty("the losing promotion creates no case");
        store.Cases.Should().ContainSingle();
        signal.PromotedEvalCaseId.Should().Be(9, "the winner's claim stands");
    }

    // The signal's SQL or rating changed after the read: the claim matches nothing and the promotion is refused.
    [Test]
    public async Task APromotionWhoseSignalChangesBeforeTheClaim_IsAConflict_AndCreatesNothing()
    {
        var store = new Store();
        var signal = Signal(1, McpUserVerdict.Correct, generatedSql: "SELECT id FROM orders");
        store.Signals.Add(signal);
        var check = WinnerCommitsDuringTheCheck(() => signal.UserCorrectedSql = "SELECT status FROM orders");

        var act = () => PromoteHandler(store, check).Handle(new PromoteSignalToGoldenCommand(1, Hash("SELECT id FROM orders")), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(PromoteSignalToGoldenHandler.ChangedSinceReviewedMessage);
        store.Saved.Should().BeEmpty();
        signal.PromotedAt.Should().BeNull();
    }

    [Test]
    public async Task APromotionRefusedByADatabaseError_FailsWithIt()
    {
        var store = new Store();
        store.Signals.Add(Signal(1, McpUserVerdict.Correct, generatedSql: "SELECT id FROM orders"));
        store.OnSave = () => throw new DbUpdateException("refused", new PostgresException("foreign key", "ERROR", "ERROR", "23503"));

        var act = () => PromoteHandler(store).Handle(new PromoteSignalToGoldenCommand(1, Hash("SELECT id FROM orders")), CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>();
        store.Capture.Commits.Should().Be(0, "the claim and the case commit together, or not at all");
    }

    private static string Hash(string sql) => GroundingContentHash.OfSql(sql);

    private static string HashOf(McpLearnedPattern pattern) => GroundingContentHash.OfPattern(pattern.PatternContent, pattern.ExampleQuestion, pattern.ExampleSql);

    // A grounding SQL check that passes the SQL after running onCheck: what a concurrent change commits between the
    // promotion's read and its claim.
    private static IGroundingSqlCheck WinnerCommitsDuringTheCheck(Action onCheck)
    {
        var check = new Mock<IGroundingSqlCheck>();
        check
            .Setup(x => x.CheckAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(onCheck)
            .ReturnsAsync((int _, string sql, CancellationToken _) => GroundingSqlCheckResult.Allowed(sql));

        return check.Object;
    }

    private static IBeaconActorAccessor NoBeaconUser()
    {
        var accessor = new Mock<IBeaconActorAccessor>();
        accessor
            .Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BeaconActor(null, true) { Kind = BeaconPrincipalKind.User, BeaconUserId = null });

        return accessor.Object;
    }

    private static UpdatePatternStatusHandler StatusHandler(Store store, IGroundingSqlCheck check)
    {
        return new UpdatePatternStatusHandler(store.Factory, check, TestProjectAccess.Actors(beaconUserId: AdminUserId), NullLogger<UpdatePatternStatusHandler>.Instance);
    }

    private static PromoteSignalToGoldenHandler PromoteHandler(Store store, IGroundingSqlCheck? check = null)
    {
        return new PromoteSignalToGoldenHandler(
            store.Factory,
            check ?? TestGroundingSqlChecks.Real(),
            TestProjectAccess.Actors(beaconUserId: AdminUserId),
            NullLogger<PromoteSignalToGoldenHandler>.Instance);
    }

    private static UpdateGoldenCaseHandler EditHandler(Store store)
    {
        return new UpdateGoldenCaseHandler(store.Factory, TestGroundingSqlChecks.Real(), TestProjectAccess.Actors(beaconUserId: AdminUserId), NullLogger<UpdateGoldenCaseHandler>.Instance);
    }

    private static McpQuerySignal Signal(int id, McpUserVerdict verdict, string? generatedSql)
    {
        return new McpQuerySignal
        {
            Id = id,
            ProjectId = ProjectId,
            DataSourceId = DataSourceId,
            Tool = "ask",
            Question = $"question {id}",
            GeneratedSql = generatedSql,
            UserVerdict = verdict,
            CreatedTime = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc).AddMinutes(id)
        };
    }

    private static McpEvalCase Case(int id, int? promotedBy)
    {
        return new McpEvalCase
        {
            Id = id,
            ProjectId = ProjectId,
            DataSourceId = DataSourceId,
            Question = "How many orders?",
            GoldSql = "SELECT id FROM orders",
            GoldResultFingerprint = "fingerprint",
            PromotedByUserId = promotedBy
        };
    }

    private static McpEvalCase PromotedCase(int id, bool isActive)
    {
        return new McpEvalCase
        {
            Id = id,
            ProjectId = ProjectId,
            DataSourceId = DataSourceId,
            Question = "question 1",
            GoldSql = "SELECT id FROM orders",
            SourceSignalId = 1,
            IsActive = isActive,
            PromotedByUserId = 4
        };
    }

    private static McpLearnedPattern Pattern(string exampleSql)
    {
        return new McpLearnedPattern
        {
            Id = 21,
            ProjectId = ProjectId,
            DataSourceId = DataSourceId,
            SchemaName = "public",
            TableName = "orders",
            PatternType = McpPatternType.CommonQuery,
            PatternContent = "Common query pattern for public.orders",
            ExampleSql = exampleSql,
            Status = McpPatternStatus.NeedsEvidence
        };
    }

    /// <summary>
    /// The signals, golden cases, users, project links, the data source and (optionally) one learned pattern every context
    /// the factory hands out serves; whatever a context adds is recorded in <see cref="Saved"/>, every save is counted, and
    /// transactions are stand-ins <see cref="Capture"/> records.
    /// </summary>
    private sealed class Store
    {
        public Store(McpLearnedPattern? pattern = null)
        {
            var links = new List<ProjectDataSource> { new() { ProjectId = ProjectId, DataSourceId = DataSourceId } };
            var sources = new List<DataSource>
            {
                new()
                {
                    Id = DataSourceId,
                    Name = "sales",
                    DataSourceType = DataSourceType.Database,
                    DatabaseEngineType = DatabaseEngineType.PostgreSQL,
                    EncryptedConnectionData = "encrypted"
                }
            };
            var patterns = RecordingBeaconContext.MemorySet(pattern == null ? new List<McpLearnedPattern>() : [pattern], Saved);
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .Callback(() => ContextsCreated++)
                .ReturnsAsync(() => new RecordingBeaconContext(
                    new Dictionary<Type, object>
                    {
                        [typeof(McpQuerySignal)] = RecordingBeaconContext.MemorySet(Signals, Saved),
                        [typeof(McpEvalCase)] = RecordingBeaconContext.MemorySet(Cases, Saved),
                        [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(Users, Saved),
                        [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(links, Saved),
                        [typeof(DataSource)] = RecordingBeaconContext.MemorySet(sources, Saved),
                        [typeof(McpLearnedPattern)] = patterns
                    },
                    Saved,
                    Capture,
                    onSave: () =>
                    {
                        SaveCount++;
                        OnSave?.Invoke();
                    }));
            Factory = factory.Object;
        }

        /// <summary>Runs on every save after it is counted; a test makes the save fail by throwing here.</summary>
        public Action? OnSave { get; set; }

        public List<McpQuerySignal> Signals { get; } = [];

        public List<McpEvalCase> Cases { get; } = [];

        public List<BeaconUser> Users { get; } = [];

        public List<object> Saved { get; } = [];

        public int SaveCount { get; private set; }

        public int ContextsCreated { get; private set; }

        public SqlCapture Capture { get; } = new();

        public IDbContextFactory<BeaconContext> Factory { get; }
    }
}

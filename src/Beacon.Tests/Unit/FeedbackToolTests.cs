using System.Reflection;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.McpEval;
using Beacon.Core.Models;
using Beacon.Core.Services.Retention;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;
using Beacon.Tests.Common;
using Beacon.Core.Mcp;

namespace Beacon.Tests.Unit;

/// <summary>
/// Part B — the <c>feedback</c> MCP tool. Verifies it routes the verdict to
/// <see cref="RecordQueryFeedbackCommand"/>, rejects an invalid verdict, and — the security-critical
/// guarantee (§1.11) — NEVER puts the user-supplied corrected SQL or note into the audit parameters or
/// the error slot. The audit is captured through a real <see cref="McpAuditService"/> over a mocked
/// context (no DB, §4.7); the corrected SQL/note contain PII sentinels that must not appear in the log.
/// </summary>
[TestFixture]
public class FeedbackToolTests
{
    private const int SignalId = 812;
    private const int SignalProjectId = 42;
    private const int ProjectlessSignalId = 813;
    private const string PiiSql = "SELECT ssn FROM patients WHERE email = 'john@example.com'";
    private const string PiiNote = "wrong — should filter to patient 12345 (john@example.com)";

    [Test]
    public async Task Correct_SendsCommandWithVerdictAndUserText()
    {
        var mediator = new Mock<IMediator>();
        mediator.Setup(x => x.Send(It.IsAny<RecordQueryFeedbackCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecordQueryFeedbackResult(false, null));

        var (tool, _) = BuildTool(mediator);

        var result = await TestToolCalls.InvokeAsync(tool, x => x.ExecuteAsync(SignalId, "correct", PiiSql, PiiNote, cancellationToken: CancellationToken.None));

        result.IsError.Should().NotBe(true);
        mediator.Verify(x => x.Send(
            It.Is<RecordQueryFeedbackCommand>(c =>
                c.SignalId == SignalId
                && c.Verdict == McpUserVerdict.Correct
                && c.CorrectedSql == PiiSql
                && c.Note == PiiNote),
            It.IsAny<CancellationToken>()), Times.Once,
            "the command carries the user text — persistence is fine; only logs/audit must exclude it");
    }

    [Test]
    public async Task InvalidVerdict_ReturnsError_NoSend_ButAudits()
    {
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        var (tool, audit) = BuildTool(mediator);

        var result = await TestToolCalls.InvokeAsync(tool, x => x.ExecuteAsync(SignalId, "maybe", null, null, cancellationToken: CancellationToken.None));

        result.IsError.Should().BeTrue();
        mediator.Verify(x => x.Send(It.IsAny<RecordQueryFeedbackCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        audit.Logs.Should().ContainSingle();
        audit.Logs[0].Tool.Should().Be("feedback");
        audit.Logs[0].ErrorMessage.Should().Contain("verdict");
        audit.Logs[0].ProjectId.Should().Be(SignalProjectId, "attribution comes from the rated signal, not the (absent) transport project context");
    }

    [Test]
    public async Task CallerCancels_PropagatesTheCancellation_AfterAuditing()
    {
        using var caller = new CancellationTokenSource();
        var mediator = new Mock<IMediator>();
        mediator.Setup(x => x.Send(It.IsAny<RecordQueryFeedbackCommand>(), It.IsAny<CancellationToken>()))
            .Returns(async (RecordQueryFeedbackCommand _, CancellationToken ct) =>
            {
                await caller.CancelAsync();
                ct.ThrowIfCancellationRequested();

                return new RecordQueryFeedbackResult(false, null);
            });
        var (tool, audit) = BuildTool(mediator);

        var act = () => TestToolCalls.InvokeAsync(tool, x => x.ExecuteAsync(SignalId, "correct", null, null, cancellationToken: caller.Token));

        await act.Should().ThrowAsync<OperationCanceledException>();
        var log = audit.Logs.Should().ContainSingle().Subject;
        log.Tool.Should().Be("feedback");
        log.ProjectId.Should().Be(SignalProjectId);
        log.ErrorMessage.Should().Be("Cancelled by the caller.");
    }

    [Test]
    public async Task DoesNotPutUserTextInAuditParametersOrError()
    {
        var mediator = new Mock<IMediator>();
        mediator.Setup(x => x.Send(It.IsAny<RecordQueryFeedbackCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecordQueryFeedbackResult(false, null));

        var (tool, audit) = BuildTool(mediator);

        await TestToolCalls.InvokeAsync(tool, x => x.ExecuteAsync(SignalId, "incorrect", PiiSql, PiiNote, cancellationToken: CancellationToken.None));

        audit.Logs.Should().ContainSingle();
        var log = audit.Logs[0];

        // §1.11 — identifiers only.
        log.Parameters.Should().Contain("signal_id=812");
        log.Parameters.Should().Contain("verdict=incorrect");
        log.Parameters.Should().NotContain(PiiSql);
        log.Parameters.Should().NotContain(PiiNote);
        log.Parameters.Should().NotContain("john@example.com");
        (log.ErrorMessage ?? "").Should().NotContain("john@example.com");
        log.ProjectId.Should().Be(SignalProjectId, "attribution comes from the rated signal, not the (absent) transport project context");
    }

    [Test]
    public async Task SignalNotFound_AuditsNullProject_AndNeverSends()
    {
        // A signal_id outside the seeded set: the tool's project lookup finds nothing (null attribution), so the
        // signal has no project to rate it in and the tool refuses it before the command.
        const int unknownSignalId = 999;
        var mediator = new Mock<IMediator>(MockBehavior.Strict);

        var (tool, audit) = BuildTool(mediator);

        var result = await TestToolCalls.InvokeAsync(tool, x => x.ExecuteAsync(unknownSignalId, "correct", null, null, cancellationToken: CancellationToken.None));

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text
            .Should().Be($"Query signal {unknownSignalId} not found.");
        audit.Logs.Should().ContainSingle();
        audit.Logs[0].ProjectId.Should().BeNull("the rated signal does not exist, so no project attribution is possible");
        audit.Logs[0].ErrorMessage.Should().Be($"Query signal {unknownSignalId} not found.");
    }

    [Test]
    public async Task SignalWithoutAProject_IsRefused_EvenForAnAdmin()
    {
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        var (tool, audit) = BuildTool(mediator, access: TestProjectAccess.Unrestricted);

        var result = await TestToolCalls.InvokeAsync(tool, x => x.ExecuteAsync(ProjectlessSignalId, "correct", null, null, cancellationToken: CancellationToken.None));

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Be($"Query signal {ProjectlessSignalId} not found.");
        audit.Logs.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be($"Query signal {ProjectlessSignalId} not found.", "every refusal is audited");
    }

    [Test]
    public async Task SignalOfAnotherProjectThanThePinnedOne_IsRefused()
    {
        // The playground pins project 7; the signal belongs to project 42, which the caller could otherwise query.
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        var (tool, audit) = BuildTool(mediator, access: TestProjectAccess.Unrestricted, activeProjectId: 7, allowedProjectIds: [7]);

        var result = await TestToolCalls.InvokeAsync(tool, x => x.ExecuteAsync(SignalId, "correct", null, null, cancellationToken: CancellationToken.None));

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Be($"Query signal {SignalId} not found.");
        audit.Logs.Should().ContainSingle()
            .Which.ProjectId.Should().Be(7, "a refusal is attributed to the pinned project, not the probed one");
    }

    [Test]
    public async Task SignalOutsideTheCallersProjectList_IsRefused()
    {
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        var (tool, _) = BuildTool(mediator, access: TestProjectAccess.Unrestricted, allowedProjectIds: [7]);

        var result = await TestToolCalls.InvokeAsync(tool, x => x.ExecuteAsync(SignalId, "correct", null, null, cancellationToken: CancellationToken.None));

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Be($"Query signal {SignalId} not found.");
    }

    [Test]
    public async Task ViewerInTheSignalsProject_IsRefused_FeedbackNeedsQuery()
    {
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        var viewer = TestProjectAccess.Scope(new Dictionary<int, ProjectRole> { [SignalProjectId] = ProjectRole.Viewer }, includesOpen: false, notOpen: []);
        var (tool, _) = BuildTool(mediator, access: viewer, activeProjectId: SignalProjectId, allowedProjectIds: [SignalProjectId]);

        var result = await TestToolCalls.InvokeAsync(tool, x => x.ExecuteAsync(SignalId, "correct", null, null, cancellationToken: CancellationToken.None));

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Be($"Query signal {SignalId} not found.");
    }

    [TestCase(7, "none", false, TestName = "Feedback_PinnedToAnotherProject_WithoutAProjectList_IsRefused")]
    [TestCase(7, "empty", false, TestName = "Feedback_PinnedToAnotherProject_WithAnEmptyProjectList_IsRefused")]
    [TestCase(SignalProjectId, "empty", false, TestName = "Feedback_InTheSignalsProject_WithAnEmptyProjectList_IsRefused")]
    [TestCase(SignalProjectId, "none", false, TestName = "Feedback_InTheSignalsProject_WithoutAProjectList_IsRefused")]
    [TestCase(SignalProjectId, "signal", true, TestName = "Feedback_InTheSignalsProject_ListedForTheCaller_IsRecorded")]
    public async Task Feedback_FollowsThePinnedProjectAndTheProjectList(int activeProjectId, string projectList, bool recorded)
    {
        var mediator = new Mock<IMediator>();
        mediator.Setup(x => x.Send(It.IsAny<RecordQueryFeedbackCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecordQueryFeedbackResult(false, null));
        var editor = TestProjectAccess.Scope(
            new Dictionary<int, ProjectRole> { [SignalProjectId] = ProjectRole.Editor, [7] = ProjectRole.Editor },
            includesOpen: false,
            notOpen: []);
        var (tool, audit) = BuildTool(
            mediator,
            access: editor,
            activeProjectId: activeProjectId,
            allowedProjectIds: projectList == "empty" ? [] : [SignalProjectId],
            withoutProjectList: projectList == "none");

        var result = await TestToolCalls.InvokeAsync(tool, x => x.ExecuteAsync(SignalId, "correct", null, null, cancellationToken: CancellationToken.None));

        result.IsError.Should().Be(recorded ? null : true);
        mediator.Verify(x => x.Send(It.IsAny<RecordQueryFeedbackCommand>(), It.IsAny<CancellationToken>()), recorded ? Times.Once() : Times.Never());
        audit.Logs.Should().ContainSingle("every call is audited, refused or not");
    }

    [Test]
    public async Task ARefusedRating_IsAuditedAndAnswered_NotThrown()
    {
        var mediator = new Mock<IMediator>();
        mediator.Setup(x => x.Send(It.IsAny<RecordQueryFeedbackCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BeaconForbiddenException("Only the caller who asked can record feedback."));
        var editor = TestProjectAccess.Scope(new Dictionary<int, ProjectRole> { [SignalProjectId] = ProjectRole.Editor }, includesOpen: false, notOpen: []);
        var (tool, audit) = BuildTool(mediator, access: editor, activeProjectId: SignalProjectId, allowedProjectIds: [SignalProjectId]);

        var result = await TestToolCalls.InvokeAsync(tool, x => x.ExecuteAsync(SignalId, "correct", null, null, cancellationToken: CancellationToken.None));

        result.IsError.Should().BeTrue();
        audit.Logs.Should().ContainSingle().Which.ErrorMessage.Should().Be("Only the caller who asked can record feedback.");
    }

    [Test]
    public async Task EditorInThePinnedSignalsProject_RecordsTheVerdict()
    {
        var mediator = new Mock<IMediator>();
        mediator.Setup(x => x.Send(It.IsAny<RecordQueryFeedbackCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecordQueryFeedbackResult(false, null));
        var editor = TestProjectAccess.Scope(new Dictionary<int, ProjectRole> { [SignalProjectId] = ProjectRole.Editor }, includesOpen: false, notOpen: []);
        var (tool, _) = BuildTool(mediator, access: editor, activeProjectId: SignalProjectId, allowedProjectIds: [SignalProjectId]);

        var result = await TestToolCalls.InvokeAsync(tool, x => x.ExecuteAsync(SignalId, "correct", null, null, cancellationToken: CancellationToken.None));

        result.IsError.Should().NotBe(true);
        mediator.Verify(x => x.Send(It.Is<RecordQueryFeedbackCommand>(y => y.SignalId == SignalId), It.IsAny<CancellationToken>()), Times.Once);
    }

    // The answer says what the verdict does, and whether the corrected SQL was kept — and why not.
    [TestCase("correct", false, null, "An Admin can promote this answer to a verified example for future answers; until then it does not change them.")]
    [TestCase("incorrect", false, null, "Beacon no longer learns common-query or join patterns from this answer.")]
    [TestCase("correct", true, null, "An Admin can promote this answer to a verified example for future answers; until then it does not change them. The corrected SQL was kept with the verdict, without its comments.")]
    [TestCase("correct", false, "the reason.", "An Admin can promote this answer to a verified example for future answers; until then it does not change them. The corrected SQL was not kept: the reason.")]
    public async Task ARecordedVerdict_SaysWhatItDoes_AndWhetherTheCorrectedSqlWasKept(string verdict, bool kept, string? reason, string answer)
    {
        var mediator = new Mock<IMediator>();
        mediator.Setup(x => x.Send(It.IsAny<RecordQueryFeedbackCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecordQueryFeedbackResult(kept, reason));
        var (tool, audit) = BuildTool(mediator);

        var result = await TestToolCalls.InvokeAsync(tool, x => x.ExecuteAsync(SignalId, verdict, kept || reason != null ? "SELECT 1" : null, null, cancellationToken: CancellationToken.None));

        result.IsError.Should().NotBe(true);
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Be($"Feedback recorded for signal {SignalId}. {answer}");
        audit.Logs.Should().ContainSingle().Which.ErrorMessage.Should().BeNull("the verdict was recorded");
    }

    // Through the real handler: corrected SQL the grounding SQL check refuses keeps the verdict and drops only the SQL; the
    // caller is told why, and the audit records a successful call with identifiers only.
    [Test]
    public async Task CorrectedSqlTheCheckRefuses_KeepsTheVerdict_DropsTheSql_AndSaysWhy()
    {
        var signal = new McpQuerySignal { Id = SignalId, ProjectId = SignalProjectId, DataSourceId = TestGroundingSqlChecks.DataSourceId, Tool = "ask", Question = "q" };
        var saved = new List<object>();
        var handlerFactory = new Mock<IDbContextFactory<BeaconContext>>();
        handlerFactory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object> { [typeof(McpQuerySignal)] = RecordingBeaconContext.MemorySet(new List<McpQuerySignal> { signal }, saved) },
                saved));
        var policy = new Mock<IContentRetentionPolicy>();
        policy
            .Setup(x => x.ResolveAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentRetentionDecision(true, true));
        var handler = new RecordQueryFeedbackHandler(
            handlerFactory.Object,
            TestGroundingSqlChecks.Real(),
            policy.Object,
            TestProjectAccess.Evaluator(TestProjectAccess.Unrestricted),
            TestProjectAccess.Actors(),
            NullLogger<RecordQueryFeedbackHandler>.Instance);
        var mediator = new Mock<IMediator>();
        mediator.Setup(x => x.Send(It.IsAny<RecordQueryFeedbackCommand>(), It.IsAny<CancellationToken>()))
            .Returns<RecordQueryFeedbackCommand, CancellationToken>((command, ct) => handler.Handle(command, ct));
        var (tool, audit) = BuildTool(mediator);

        var result = await TestToolCalls.InvokeAsync(tool, x => x.ExecuteAsync(SignalId, "correct", "DELETE FROM orders", null, cancellationToken: CancellationToken.None));

        result.IsError.Should().NotBe(true);
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain(" The corrected SQL was not kept: it did not pass the SQL check: ");
        var log = audit.Logs.Should().ContainSingle("the call is audited").Subject;
        log.ErrorMessage.Should().BeNull();
        log.Parameters.Should().Be($"signal_id={SignalId};verdict=correct");
        signal.UserVerdict.Should().Be(McpUserVerdict.Correct, "the verdict stands");
        signal.UserCorrectedSql.Should().BeNull("only the SQL is dropped");
    }

    // Beacon:RateLimits:Feedback: a call over the caller's window is refused before the signal is looked up, answered with
    // when to retry, audited, and records no verdict.
    [Test]
    public async Task OverTheFeedbackLimit_IsRefusedWithARetry_AuditedBeforeAnyLookup_AndRecordsNoVerdict()
    {
        const int unknownSignalId = 999;
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        var limiter = new McpRateLimiter(Options.Create(new RateLimitOptions { Feedback = new RateLimitWindow { PermitLimit = 1 } }));
        var (tool, audit) = BuildTool(mediator, limiter: limiter);

        await TestToolCalls.InvokeAsync(tool, x => x.ExecuteAsync(unknownSignalId, "correct", cancellationToken: CancellationToken.None));
        var readsBefore = audit.SignalReads.Count;
        var refused = await TestToolCalls.InvokeAsync(tool, x => x.ExecuteAsync(SignalId, "incorrect", PiiSql, PiiNote, "wrong_join", CancellationToken.None));

        readsBefore.Should().Be(1, "the call within the limit looked its signal up");
        refused.IsError.Should().BeTrue();
        var text = refused.Content.OfType<TextContentBlock>().Single().Text;
        text.Should().MatchRegex("^Too many feedback calls for this credential; retry in [0-9]+ seconds\\.$");
        audit.SignalReads.Count.Should().Be(readsBefore, "the refused call never reads the store");
        audit.Logs.Should().HaveCount(2, "every call, the refused one included, writes its audit row");
        audit.Logs[1].Tool.Should().Be("feedback");
        audit.Logs[1].ErrorMessage.Should().Be(text);
        audit.Logs[1].Parameters.Should().Be($"signal_id={SignalId};verdict=incorrect;reason=wrong_join");
        mediator.Verify(x => x.Send(It.IsAny<RecordQueryFeedbackCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The tool's code table names every reason the handler accepts, each once and under one code: a reason added to the enum
    // without a code (or twice) fails here.
    [Test]
    public void TheReasonCodeTable_CoversEveryFeedbackReasonExactlyOnce()
    {
        var field = typeof(FeedbackTool).GetField("Reasons", BindingFlags.NonPublic | BindingFlags.Static);
        field.Should().NotBeNull("the tool keeps its reason codes in the static Reasons table");

        var table = (ValueTuple<string, McpFeedbackReason>[])field!.GetValue(null)!;

        table.Select(x => x.Item2).Should().BeEquivalentTo(Enum.GetValues<McpFeedbackReason>(), options => options.WithoutStrictOrdering());
        table.Select(x => x.Item2).Should().OnlyHaveUniqueItems();
        table.Select(x => x.Item1).Should().OnlyHaveUniqueItems().And.OnlyContain(x => x == x.Trim().ToLowerInvariant());
    }

    [TestCase("wrong_table", McpFeedbackReason.WrongTable, "wrong_table")]
    [TestCase("wrong_column", McpFeedbackReason.WrongColumn, "wrong_column")]
    [TestCase("wrong_join", McpFeedbackReason.WrongJoin, "wrong_join")]
    [TestCase("wrong_filter", McpFeedbackReason.WrongFilter, "wrong_filter")]
    [TestCase("wrong_aggregation", McpFeedbackReason.WrongAggregation, "wrong_aggregation")]
    [TestCase("wrong_time_range", McpFeedbackReason.WrongTimeRange, "wrong_time_range")]
    [TestCase("misunderstood_question", McpFeedbackReason.MisunderstoodQuestion, "misunderstood_question")]
    [TestCase("other", McpFeedbackReason.Other, "other")]
    [TestCase("  Wrong_Time_Range ", McpFeedbackReason.WrongTimeRange, "wrong_time_range")]
    public async Task AReasonFromTheList_IsSentWithTheVerdict_AndAuditedByItsCode(string reason, McpFeedbackReason expected, string code)
    {
        var mediator = new Mock<IMediator>();
        mediator.Setup(x => x.Send(It.IsAny<RecordQueryFeedbackCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecordQueryFeedbackResult(false, null));
        var (tool, audit) = BuildTool(mediator);

        var result = await TestToolCalls.InvokeAsync(tool, x => x.ExecuteAsync(SignalId, "incorrect", reason: reason, cancellationToken: CancellationToken.None));

        result.IsError.Should().NotBe(true);
        mediator.Verify(x => x.Send(
            It.Is<RecordQueryFeedbackCommand>(y => y.SignalId == SignalId && y.Verdict == McpUserVerdict.Incorrect && y.Reason == expected),
            It.IsAny<CancellationToken>()), Times.Once);
        audit.Logs.Should().ContainSingle().Which.Parameters.Should().Be($"signal_id={SignalId};verdict=incorrect;reason={code}");
    }

    // A reason outside the list is a validation error naming the allowed codes; the value sent reaches neither the answer
    // nor the audit row (§1.11), and no verdict is recorded.
    [Test]
    public async Task AReasonOutsideTheList_IsRefused_NamingTheAllowedCodes_AndNeverAuditsTheValue()
    {
        const string rawReason = "wrong patient: john@example.com";
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        var (tool, audit) = BuildTool(mediator);

        var result = await TestToolCalls.InvokeAsync(tool, x => x.ExecuteAsync(SignalId, "incorrect", reason: rawReason, cancellationToken: CancellationToken.None));

        result.IsError.Should().BeTrue();
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        text.Should().Be("reason must be one of wrong_table, wrong_column, wrong_join, wrong_filter, wrong_aggregation, wrong_time_range, misunderstood_question, other.");
        mediator.Verify(x => x.Send(It.IsAny<RecordQueryFeedbackCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        var log = audit.Logs.Should().ContainSingle().Subject;
        log.Parameters.Should().Be($"signal_id={SignalId};verdict=incorrect;reason=invalid");
        log.Parameters.Should().NotContain("john@example.com");
        log.ErrorMessage.Should().Be(text);
        log.ProjectId.Should().Be(SignalProjectId);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task NoReason_SendsNone_AndLeavesTheAuditParametersAsTheyWere(string? reason)
    {
        var mediator = new Mock<IMediator>();
        mediator.Setup(x => x.Send(It.IsAny<RecordQueryFeedbackCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecordQueryFeedbackResult(false, null));
        var (tool, audit) = BuildTool(mediator);

        var result = await TestToolCalls.InvokeAsync(tool, x => x.ExecuteAsync(SignalId, "incorrect", reason: reason, cancellationToken: CancellationToken.None));

        result.IsError.Should().NotBe(true);
        mediator.Verify(x => x.Send(It.Is<RecordQueryFeedbackCommand>(y => y.Reason == null), It.IsAny<CancellationToken>()), Times.Once);
        audit.Logs.Should().ContainSingle().Which.Parameters.Should().Be($"signal_id={SignalId};verdict=incorrect");
    }

    private static (FeedbackTool Tool, AuditCapture Audit) BuildTool(
        Mock<IMediator> mediator,
        ProjectAccessScope? access = null,
        int? activeProjectId = null,
        List<int>? allowedProjectIds = null,
        bool withoutProjectList = false,
        McpRateLimiter? limiter = null)
    {
        // One factory serves both the tool (signal → project lookup) and the audit service; each
        // CreateDbContextAsync call gets a fresh context over the same captured log list (§4.7 —
        // async-queryable doubles, no DB).
        var logs = new List<McpAuditLog>();
        var signalReads = new SignalReads();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new CapturingAuditContext(logs, signalReads));

        // Each tool gets its own limiter (default limits unless one is given), so no test spends another's permits.
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(x => x.GetService(typeof(McpRateLimiter))).Returns(limiter ?? new McpRateLimiter());

        var auditService = new McpAuditService(factory.Object, SettingsProviderMock.Create().Object, new HttpContextAccessor(), Options.Create(new McpDeploymentOptions()), NullLogger<McpAuditService>.Instance, new McpAuditOutcome(), Options.Create(new BeaconTelemetryOptions()), NullLoggerFactory.Instance, TestMcpAudit.ContentHasher, new McpStatementLog());

        var projectContext = new Mock<IProjectContext>();
        projectContext.SetupGet(x => x.UserId).Returns(1);
        projectContext.SetupGet(x => x.ActiveProjectId).Returns(activeProjectId);
        // The caller's projects default to the signal's; without a list (no caller resolved) nothing is reachable.
        projectContext.SetupGet(x => x.AllowedProjectIds).Returns(withoutProjectList ? null : allowedProjectIds ?? [SignalProjectId]);
        projectContext.SetupGet(x => x.ApiKeyId).Returns((int?)null);

        var tool = TestToolCalls.Track(recorder => new FeedbackTool(
            factory.Object,
            projectContext.Object,
            recorder,
            mediator.Object,
            TestProjectAccess.Evaluator(access ?? TestProjectAccess.Unrestricted),
            serviceProvider.Object), () => TestToolCalls.Recorder(auditService));

        return (tool, new AuditCapture(logs, signalReads));
    }

    private sealed record AuditCapture(List<McpAuditLog> Logs, SignalReads SignalReads);

    /// <summary>How often a context served the signal set, i.e. how often the tool looked a signal up.</summary>
    private sealed class SignalReads
    {
        public int Count { get; set; }
    }

    /// <summary>Real McpAuditService writes McpAuditLog rows through this context; we capture them to assert
    /// no user text leaked into the audit trail. Also serves a seeded McpQuerySignal set so the tool can
    /// resolve the rated signal's project for audit attribution.</summary>
    private sealed class CapturingAuditContext : BeaconContext
    {
        private static readonly DbContextOptions<CapturingAuditContext> Options =
            new DbContextOptionsBuilder<CapturingAuditContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly Mock<DbSet<McpAuditLog>> _set = new();
        private readonly SignalReads _signalReads;

        public List<McpAuditLog> Logs { get; }

        public CapturingAuditContext(List<McpAuditLog> logs, SignalReads signalReads) : base(Options, "beacon")
        {
            Logs = logs;
            _signalReads = signalReads;
            _set.Setup(x => x.Add(It.IsAny<McpAuditLog>()))
                .Callback<McpAuditLog>(x => Logs.Add(x));
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpAuditLog))
            {
                return (DbSet<TEntity>)(object)_set.Object;
            }

            if (typeof(TEntity) == typeof(McpQuerySignal))
            {
                _signalReads.Count++;
                return (DbSet<TEntity>)(object)BuildSignalSet();
            }

            return base.Set<TEntity>();
        }

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        private static DbSet<McpQuerySignal> BuildSignalSet()
        {
            var data = new List<McpQuerySignal>
            {
                new()
                {
                    Id = SignalId,
                    ProjectId = SignalProjectId,
                    Tool = "ask",
                    Question = "q"
                },
                new()
                {
                    Id = ProjectlessSignalId,
                    ProjectId = null,
                    Tool = "ask",
                    Question = "q"
                }
            }.AsQueryable();

            var set = new Mock<DbSet<McpQuerySignal>>();
            set.As<IAsyncEnumerable<McpQuerySignal>>()
                .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                .Returns(new TestAsyncEnumerator<McpQuerySignal>(data.GetEnumerator()));
            set.As<IQueryable<McpQuerySignal>>()
                .Setup(x => x.Provider)
                .Returns(new TestAsyncQueryProvider<McpQuerySignal>(data.Provider));
            set.As<IQueryable<McpQuerySignal>>().Setup(x => x.Expression).Returns(data.Expression);
            set.As<IQueryable<McpQuerySignal>>().Setup(x => x.ElementType).Returns(data.ElementType);
            set.As<IQueryable<McpQuerySignal>>().Setup(x => x.GetEnumerator()).Returns(data.GetEnumerator());
            return set.Object;
        }
    }
}

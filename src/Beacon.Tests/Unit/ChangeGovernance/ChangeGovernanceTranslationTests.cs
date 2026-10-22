using Beacon.AI.Handlers.AiActors;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.Ai.AiActor.Models;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Queries;
using Beacon.Core.Handlers.QueryVersions;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Queries;
using Beacon.Core.SavedQueries;
using Beacon.Core.Services;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// The SQL the change-governance paths send, on PostgreSQL and SQL Server, captured without a database: a submission, an
/// approval and a rejection each take the query's row first (a no-op update) and read after it; a decision is a
/// conditional update from Pending, the query moves to the proposal only from the version read (also when it had none),
/// a submission supersedes the pending requests with conditional updates and a lost version number answers 409, a run
/// reads the active version with the approved hash in one statement, strict mode binds an MCP tool's version to its
/// approved hash, an AI plan is decided by a conditional update from PendingApproval, and the lock, the plan's creator and
/// revision chain and an MCP tool's deciders are read by id.
/// </summary>
[TestFixture]
public class ChangeGovernanceTranslationTests
{
    private static readonly string EmptyContent = QueryContentHash.Of(null, Array.Empty<QueryStepSnapshot>()).Hash;

    [TestCase(false)]
    [TestCase(true)]
    public async Task Approve_TheDecisionAndTheMove_AreConditionalUpdates_Translates(bool sqlServer)
    {
        var capture = Capture(sqlServer)
            .ThenRow(7, 102, (int)ApprovalStatus.Pending, "ext-ben", DBNull.Value, 101, "Orders", DBNull.Value, DBNull.Value, "[]", EmptyContent, 101)
            .ThenNoRows()
            .ThenRowsAffected(1)
            .ThenRowsAffected(1)
            .ThenRowsAffected(1)
            .ThenNoRows()
            .ThenNoRows();

        await TestQueryServices.ApprovalService(capture.Factory()).ApproveAsync(90, null, EmptyContent, CancellationToken.None);

        capture.Commands.Should().HaveCount(7);
        capture.Commits.Should().Be(1);
        ShouldTakeTheQuerysRow(capture.Commands[2], sqlServer);
        if (sqlServer)
        {
            capture.Commands[0].Should().Contain("[beacon].[QueryApprovalRequests]").And.Contain("[q0].[ContentHash]").And.Contain("[q].[ContributorUserIds]");
            capture.Commands[3].Should().Contain("UPDATE [q]").And.Contain("[q].[ApprovedContentHash] = @").And.Contain("FROM [beacon].[QueryApprovalRequests]").And.EndWith("AND [q].[Status] = 1");
            capture.Commands[4].Should().Contain("[q].[ActiveVersionId] = @p,").And.Contain("FROM [beacon].[Queries]").And.Contain("AND [q].[ActiveVersionId] = @request_ActiveVersionId");
            capture.Commands[4].Should().Contain("[q].[ModifiedByUserId] = @").And.Contain("[q].[ModifiedTime] = @", "the reviewer who made the change live is the query's last modifier");
            capture.Commands[6].Should().Contain("[q].[Status] = 1 OR [q].[Id] = @request_QueryVersionId");
            return;
        }

        capture.Commands[0].Should().Contain("FROM beacon.query_approval_requests").And.Contain("q0.content_hash").And.Contain("q.contributor_user_ids");
        capture.Commands[3].Should().StartWith("UPDATE beacon.query_approval_requests").And.Contain("approved_content_hash = @").And.EndWith("AND q.status = 1");
        capture.Commands[4].Should().StartWith("UPDATE beacon.queries").And.Contain("SET active_version_id = @p,").And.EndWith("AND q.active_version_id = @request_ActiveVersionId");
        capture.Commands[4].Should().Contain("modified_by_user_id = @").And.Contain("modified_time = @", "the reviewer who made the change live is the query's last modifier");
        capture.Commands[6].Should().Contain("q.status = 1 OR q.id = @request_QueryVersionId");
    }

    // A query that had no active version moves to the proposal only while it still has none.
    [TestCase(false, "q.active_version_id IS NULL")]
    [TestCase(true, "[q].[ActiveVersionId] IS NULL")]
    public async Task Approve_OnAQueryWithoutAnActiveVersion_MovesItOnlyWhileItHasNone_Translates(bool sqlServer, string condition)
    {
        var capture = Capture(sqlServer)
            .ThenRow(7, 102, (int)ApprovalStatus.Pending, "ext-ben", DBNull.Value, DBNull.Value, "Orders", DBNull.Value, DBNull.Value, "[]", EmptyContent, DBNull.Value)
            .ThenNoRows()
            .ThenRowsAffected(1)
            .ThenRowsAffected(1)
            .ThenRowsAffected(1)
            .ThenNoRows()
            .ThenNoRows();

        await TestQueryServices.ApprovalService(capture.Factory()).ApproveAsync(90, null, EmptyContent, CancellationToken.None);

        capture.Commands[4].Should().EndWith(condition);
    }

    // A decision that matches no row (decided or superseded since it was read) answers 409: nothing is committed and nothing
    // runs after it.
    [TestCase(false)]
    [TestCase(true)]
    public async Task Approve_WhenTheDecisionMatchesNoRow_Is409AndCommitsNothing(bool sqlServer)
    {
        var capture = Approval(sqlServer, activeVersionId: 101)
            .ThenRowsAffected(1)
            .ThenRowsAffected(0);

        var act = () => TestQueryServices.ApprovalService(capture.Factory()).ApproveAsync(90, null, EmptyContent, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(QueryApprovalService.AlreadyDecidedMessage);
        capture.Commands.Should().HaveCount(4, "the row, the reviewer's label, the query's row and the decision; no move, no steps");
        capture.Commits.Should().Be(0);
    }

    // A move that matches no row (another version went live since the request was read) is stale: the decision made in the
    // same transaction is not committed, and no steps are written.
    [TestCase(false)]
    [TestCase(true)]
    public async Task Approve_WhenTheMoveMatchesNoRow_IsStale409AndCommitsNothing(bool sqlServer)
    {
        var capture = Approval(sqlServer, activeVersionId: 101)
            .ThenRowsAffected(1)
            .ThenRowsAffected(1)
            .ThenRowsAffected(0);

        var act = () => TestQueryServices.ApprovalService(capture.Factory()).ApproveAsync(90, null, EmptyContent, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(QueryApprovalService.StaleMessage);
        capture.Commands.Should().HaveCount(5, "the move is the last statement: no steps and no versions are read or written");
        capture.Commits.Should().Be(0);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Reject_WhenTheDecisionMatchesNoRow_Is409AndCommitsNothing(bool sqlServer)
    {
        var capture = Capture(sqlServer)
            .ThenRow(7, (int)ApprovalStatus.Pending, 102, "ext-ben")
            .ThenNoRows()
            .ThenRowsAffected(1)
            .ThenRowsAffected(0);

        var act = () => TestQueryServices.ApprovalService(capture.Factory()).RejectAsync(90, null, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(QueryApprovalService.AlreadyDecidedMessage);
        capture.Commands.Should().HaveCount(4, "the proposal is not marked Rejected");
        capture.Commits.Should().Be(0);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Reject_IsAConditionalUpdate_AndMarksOnlyAPendingProposal_Translates(bool sqlServer)
    {
        var capture = Capture(sqlServer)
            .ThenRow(7, (int)ApprovalStatus.Pending, 102, "ext-ben")
            .ThenNoRows()
            .ThenRowsAffected(1)
            .ThenRowsAffected(1)
            .ThenRowsAffected(1);

        await TestQueryServices.ApprovalService(capture.Factory()).RejectAsync(90, null, CancellationToken.None);

        capture.Commands.Should().HaveCount(5);
        capture.Commits.Should().Be(1);
        ShouldTakeTheQuerysRow(capture.Commands[2], sqlServer);
        capture.Commands[3].Should().EndWith(sqlServer ? "AND [q].[Status] = 1" : "AND q.status = 1");
        capture.Commands[4].Should().Contain(sqlServer ? "FROM [beacon].[QueryVersions]" : "UPDATE beacon.query_versions")
            .And.EndWith(sqlServer ? "AND [q].[Status] = 3" : "AND q.status = 3");
    }

    // The query's row is the first thing the submission's transaction writes, as in a decision's: two submissions, or a
    // submission and a decision, on one query run one after the other in the same lock order. The active version (the base)
    // and the pending requests are read after it.
    [TestCase(false)]
    [TestCase(true)]
    public async Task Submit_TakesTheQuerysRowFirst_ThenSupersedesThePendingRequests_WithConditionalUpdates_Translates(bool sqlServer)
    {
        var capture = Submission(sqlServer);

        var act = () => Submit(capture);

        await act.Should().ThrowAsync<DbUpdateException>("the insert is captured, not run");
        ShouldTakeTheQuerysRow(capture.Commands[2], sqlServer);
        capture.Commands[3].Should().Contain(sqlServer ? "[q].[ActiveVersionId]" : "q.active_version_id");
        capture.Commands[4].Should().Contain(sqlServer ? "FROM [beacon].[QueryApprovalRequests]" : "FROM beacon.query_approval_requests")
            .And.Contain(sqlServer ? "[q].[RequestedByUserId]" : "q.requested_by_user_id");
        if (sqlServer)
        {
            capture.Commands[5].Should().Contain("FROM [beacon].[QueryVersions]").And.Contain("[q].[QueryId] = @queryId AND [q].[Status] = 3 AND EXISTS (").And.Contain("[q0].[Status] = 1)");
            capture.Commands[6].Should().Contain("FROM [beacon].[QueryApprovalRequests]").And.EndWith("[q].[QueryId] = @queryId AND [q].[Status] = 1");
        }
        else
        {
            capture.Commands[5].Should().StartWith("UPDATE beacon.query_versions").And.Contain("q.query_id = @queryId AND q.status = 3 AND EXISTS (").And.Contain("q0.status = 1)");
            capture.Commands[6].Should().StartWith("UPDATE beacon.query_approval_requests").And.EndWith("q.query_id = @queryId AND q.status = 1");
        }

        capture.Commands[9].Should().Contain("INSERT", "the new version follows the supersede in the same transaction");
        capture.Commits.Should().Be(0);
    }

    // Two writes of one query's versions that took the same number (a save with the workflow off does not take the query's
    // row): the unique index refuses the loser, which answers 409 and writes nothing.
    [Test]
    public async Task Submit_ThatLosesTheVersionNumberToAConcurrentWrite_Is409()
    {
        var capture = Submission(sqlServer: false)
            .ThenThrow(new PostgresException("duplicate key value violates unique constraint", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation));

        var act = () => Submit(capture);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(QueryVersionService.ConcurrentWriteMessage);
        capture.Commits.Should().Be(0);
    }

    [TestCase(false, "q1.status = 2 AND q1.approved_content_hash IS NOT NULL AND q1.approved_content_hash = q0.content_hash")]
    [TestCase(true, "[q1].[Status] = 2 AND [q1].[ApprovedContentHash] IS NOT NULL AND [q1].[ApprovedContentHash] = [q0].[ContentHash]")]
    public async Task TheActiveVersionARunComparesWith_IsReadInOneStatement_Translates(bool sqlServer, string approvedForItsHash)
    {
        var capture = Capture(sqlServer).ThenNoRows();
        await using var context = await capture.Factory().CreateDbContextAsync();

        await ReviewedQueryContent.ActiveVersionAsync(context, 7, CancellationToken.None);

        capture.Commands.Should().ContainSingle().Which.Should().Contain(approvedForItsHash).And.Contain("EXISTS (");
    }

    [TestCase(false, "q.query_version_id = @versionId AND q.status = 2")]
    [TestCase(true, "[q].[QueryVersionId] = @versionId AND [q].[Status] = 2")]
    public void TheDecidersOfAnMcpToolsVersion_Translates(bool sqlServer, string filter)
    {
        using var context = sqlServer ? (Beacon.Core.Data.BeaconContext)SqlServerTestContext.Create() : NpgsqlTestContext.Create();
        var versionId = 5;

        // The chain SetQueryMcpToolHandler reads the deciders with.
        var sql = context.QueryApprovalRequests
            .Where(x => x.QueryVersionId == versionId)
            .Where(x => x.Status == ApprovalStatus.Approved)
            .Select(x =>
                new
                {
                    x.RequestedByUserId,
                    x.ReviewedByUserId
                })
            .ToQueryString();

        sql.Should().Contain(filter).And.Contain(sqlServer ? "[q].[ReviewedByUserId]" : "q.reviewed_by_user_id");
    }

    [TestCase(false, "q0.is_locked")]
    [TestCase(true, "[q0].[IsLocked]")]
    public async Task TheLockOfTheQueryARestoreTargets_Translates(bool sqlServer, string column)
    {
        var capture = Capture(sqlServer).ThenRow(7, false);
        var versions = new Mock<IQueryVersionService>();
        versions
            .Setup(x => x.RestoreVersionAsync(5, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryVersionChange(new QueryVersion { Id = 6, QueryId = 7, VersionNumber = 3, Status = QueryVersionStatus.Active, Name = "Orders", StepsJson = "[]" }, 0));
        var handler = new RestoreQueryVersionHandler(versions.Object, capture.Factory(), TestProjectAccess.Actors(), TestProjectAccess.Authorizer(TestProjectAccess.Unrestricted));

        await handler.Handle(new RestoreQueryVersionCommand { VersionId = 5 }, CancellationToken.None);

        capture.Commands.Should().ContainSingle().Which.Should().Contain(column);
    }

    [TestCase(false, "FROM beacon.ai_actor_plans")]
    [TestCase(true, "FROM [beacon].[AiActorPlans]")]
    public async Task ThePlanCreatorAnApprovalComparesWith_Translates(bool sqlServer, string table)
    {
        // The plan (actor 9, created by ext-ana), its parent (30), and the parent's own parent (none), sent back for revision.
        var capture = Capture(sqlServer)
            .ThenRow(9, "ext-ana")
            .ThenScalar(30)
            .ThenRow(DBNull.Value, (int)AiActorPlanStatus.RevisionRequested, "ext-cid");
        var workflowOn = new BeaconConfiguration { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true } };

        await AiActorOwnership.EnsureMayApprovePlanAsync(
            TestProjectAccess.Actors(),
            TestPermissions.Holding(BeaconPermissions.Approver),
            capture.Factory(),
            workflowOn,
            31,
            NullLogger.Instance,
            CancellationToken.None);

        capture.Commands.Should().HaveCount(3).And.AllSatisfy(x => x.Should().Contain(table));
        capture.Commands[1].Should().Contain(sqlServer ? "[a].[ParentPlanId]" : "a.parent_plan_id");
        capture.Commands[2].Should().Contain(sqlServer ? "[a].[ReviewedByUserId]" : "a.reviewed_by_user_id");
        capture.CommandParameters[2].Values.Should().Contain(30, "the chain is walked parent by parent, by id");
    }

    // Strict mode: an MCP tool's active version counts as approved only through an approval of exactly its content.
    [TestCase(false, "q1.status = 2 AND q1.approved_content_hash IS NOT NULL AND q1.approved_content_hash = q0.content_hash")]
    [TestCase(true, "[q1].[Status] = 2 AND [q1].[ApprovedContentHash] IS NOT NULL AND [q1].[ApprovedContentHash] = [q0].[ContentHash]")]
    public void TheRunnableVersionInStrictMode_RequiresTheApprovedHash_Translates(bool sqlServer, string approvedForItsHash)
    {
        using var context = sqlServer ? (Beacon.Core.Data.BeaconContext)SqlServerTestContext.Create() : NpgsqlTestContext.Create();

        var strict = SavedQueryRunnableVersion.ForQuery(context, 5, requireApprovedHash: true).ToQueryString();
        var lenient = SavedQueryRunnableVersion.ForQuery(context, 5).ToQueryString();

        strict.Should().Contain(approvedForItsHash);
        lenient.Should().NotContain(sqlServer ? "[ApprovedContentHash]" : "approved_content_hash");
    }

    // What the startup note counts: queries with steps and no active version, and tools on approvals without a hash.
    [TestCase(false)]
    [TestCase(true)]
    public async Task TheStartupCounts_Translate(bool sqlServer)
    {
        var capture = Capture(sqlServer).ThenScalar(0).ThenNoRows();

        await ApprovalWorkflowReport.CountUnversionedQueriesAsync(capture.Factory(), CancellationToken.None);
        await ApprovalWorkflowReport.ToolsOnApprovalsWithoutAHashAsync(capture.Factory(), CancellationToken.None);

        capture.Commands[0].Should().Contain(sqlServer ? "[q].[ActiveVersionId] IS NULL" : "q.active_version_id IS NULL").And.Contain("EXISTS (");
        capture.Commands[1].Should().Contain(sqlServer ? "[q].[McpToolEnabled] = CAST(1 AS bit)" : "q.mcp_tool_enabled")
            .And.Contain("NOT EXISTS (")
            .And.Contain(sqlServer ? "[q2].[ApprovedContentHash] = [q0].[ContentHash]" : "q2.approved_content_hash = q0.content_hash");
    }

    // What the startup note counts of what does not run: the queries with subscriptions and an active version, read with
    // their steps in one statement (compared in memory), and the active versions without an approval of their hash.
    [TestCase(false)]
    [TestCase(true)]
    public async Task TheStartupCountsOfWhatDoesNotRun_Translate(bool sqlServer)
    {
        var capture = Capture(sqlServer).ThenNoRows().ThenScalar(0);

        await ApprovalWorkflowReport.CountDriftedScheduledQueriesAsync(capture.Factory(), CancellationToken.None);
        await ApprovalWorkflowReport.CountActiveVersionsNotApprovedForTheirHashAsync(capture.Factory(), CancellationToken.None);

        capture.Commands.Should().HaveCount(2);
        capture.Commands[0].Should().Contain(sqlServer ? "[q].[ActiveVersionId] IS NOT NULL" : "q.active_version_id IS NOT NULL")
            .And.Contain(sqlServer ? "FROM [beacon].[Subscriptions]" : "FROM beacon.subscriptions")
            .And.Contain(sqlServer ? "[beacon].[QueryStepParameters]" : "beacon.query_step_parameters");
        capture.Commands[1].Should().Contain("NOT EXISTS (")
            .And.Contain(sqlServer ? "[q].[McpToolEnabled] = CAST(1 AS bit)" : "q.mcp_tool_enabled")
            .And.Contain(sqlServer ? "[q1].[ApprovedContentHash] = [q0].[ContentHash]" : "q1.approved_content_hash = q0.content_hash");
    }

    // An AI actor's plan is decided only from PendingApproval and before it expired, in one conditional update recording
    // who decided.
    [TestCase(false, "AND a.status = 1")]
    [TestCase(true, "AND [a].[Status] = 1")]
    public async Task RejectingAPlan_IsAConditionalUpdateFromPendingApproval_Translates(bool sqlServer, string fromPending)
    {
        var capture = Capture(sqlServer).ThenRowsAffected(1);

        await AiActorServices(capture).RejectPlanAsync(new RejectPlanOptions { PlanId = 31, UserId = "ext-ben", Reason = "no" }, CancellationToken.None);

        capture.Commands.Should().ContainSingle().Which.Should().EndWith(fromPending)
            .And.Contain(sqlServer ? "[a].[ReviewedByUserId] = @" : "reviewed_by_user_id = @")
            .And.Contain(sqlServer ? "[a].[ProposedAt] > @" : "a.proposed_at > @");
    }

    // Nothing decided: a second read says whether the plan expired undecided or was decided already.
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task DecidingAPlanThatIsNoLongerPending_Is409(bool sqlServer, bool expired)
    {
        var capture = Capture(sqlServer).ThenRowsAffected(0).ThenScalar(expired);

        var act = () => AiActorServices(capture).RejectPlanAsync(new RejectPlanOptions { PlanId = 31, UserId = "ext-ben", Reason = "no" }, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>()
            .WithMessage(expired ? AiActorService.PlanExpiredMessage : AiActorService.PlanNotPendingMessage);
        capture.Commands.Should().HaveCount(2);
        capture.Commands[1].Should().Contain(sqlServer ? "[a].[ProposedAt] <= @" : "a.proposed_at <= @");
    }

    // The query's row is taken with a no-op update: a row lock on both providers, held until the transaction ends.
    private static void ShouldTakeTheQuerysRow(string sql, bool sqlServer)
    {
        if (sqlServer)
        {
            sql.Should().StartWith("UPDATE [q]").And.Contain("SET [q].[ActiveVersionId] = [q].[ActiveVersionId]").And.Contain("FROM [beacon].[Queries] AS [q]").And.EndWith("[q].[Id] = @queryId");
            return;
        }

        sql.Should().StartWith("UPDATE beacon.queries AS q").And.Contain("SET active_version_id = q.active_version_id").And.EndWith("q.id = @queryId");
    }

    // A submission scripted up to its insert: the data sources, the requester's label, the query's row, the query (active
    // version 101), no pending request, the two supersedes, the highest version number (2) and no stored steps.
    private static SqlCapture Submission(bool sqlServer)
    {
        return Capture(sqlServer)
            .ThenNoRows()
            .ThenNoRows()
            .ThenRowsAffected(1)
            .ThenRow(101, "Orders", DBNull.Value, DBNull.Value)
            .ThenNoRows()
            .ThenRowsAffected(1)
            .ThenRowsAffected(1)
            .ThenScalar(2)
            .ThenNoRows();
    }

    // An approval read up to its transaction: the pending request 90 (version 102 of query 7, requested by ext-ben, made on
    // the active version, with empty content) and the reviewer's label.
    private static SqlCapture Approval(bool sqlServer, int activeVersionId)
    {
        return Capture(sqlServer)
            .ThenRow(7, 102, (int)ApprovalStatus.Pending, "ext-ben", DBNull.Value, activeVersionId, "Orders", DBNull.Value, DBNull.Value, "[]", EmptyContent, activeVersionId)
            .ThenNoRows();
    }

    private static Task<QueryVersionChange> Submit(SqlCapture capture)
    {
        return TestQueryServices.VersionService(capture.Factory()).SubmitForApprovalAsync(
            7,
            new QueryData { Name = "Orders" },
            new QueryChangeSubmission("ext-ada", null, "UserEdit", null, "SQL query modified"),
            CancellationToken.None);
    }

    private static AiActorService AiActorServices(SqlCapture capture) =>
        new(
            capture.Factory(),
            Mock.Of<ILlmProvider>(MockBehavior.Strict),
            Mock.Of<IDatabaseMetadataService>(MockBehavior.Strict),
            Mock.Of<IQueryService>(MockBehavior.Strict),
            Mock.Of<ISubscriptionService>(MockBehavior.Strict),
            Mock.Of<IBeaconScheduler>(MockBehavior.Strict),
            Mock.Of<IRunAsScope>(),
            TestProjectAccess.ExecutionAllowed(),
            Mock.Of<IStepSqlSaveCheck>(),
            Mock.Of<IQueryProposalCheck>(MockBehavior.Strict),
            Mock.Of<IQueryVersionService>(MockBehavior.Strict),
            new BeaconConfiguration { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true } },
            new RecordingSecurityAudit(),
            NullLogger<AiActorService>.Instance);

    private static SqlCapture Capture(bool sqlServer) => sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture();
}

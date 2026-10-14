using System.Text.Json;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.QueryVersions;
using Beacon.Core.Models;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// Restoring a query version puts back only a version that was live, never a proposal nobody approved; its SQL passes the
/// save check again; with the approval workflow on it is submitted for approval instead of going live; and a query someone
/// else locked is restored only by a caller who may override the lock (Manage), its lock holder needing Write only.
/// </summary>
[TestFixture]
public class RestoreVersionTests
{
    [TestCase(QueryVersionStatus.PendingApproval)]
    [TestCase(QueryVersionStatus.Rejected)]
    [TestCase(QueryVersionStatus.Superseded)]
    public async Task Restoring_AVersionThatWasNeverApproved_IsRefused(QueryVersionStatus status)
    {
        var store = new GovernanceStore();
        var active = store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var proposal = store.AddVersion(2, status, "SELECT 2");

        var act = () => TestQueryServices.VersionService(store.Factory(), configuration: WorkflowOn()).RestoreVersionAsync(proposal.Id, "ext-ada", CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage("*never approved*");
        store.Saved.Should().BeEmpty();
        store.Query.ActiveVersionId.Should().Be(active.Id);
    }

    [Test]
    public async Task Restoring_WithTheWorkflowOff_GoesLiveAsANewActiveVersion()
    {
        var store = new GovernanceStore();
        store.User(3, "ext-ada", "Ada Lovelace");
        var old = store.AddVersion(1, QueryVersionStatus.Archived, "SELECT 0");
        var active = store.Activate(store.AddVersion(2, QueryVersionStatus.Active));

        var restored = await TestQueryServices.VersionService(store.Factory()).RestoreVersionAsync(old.Id, "ext-ada", CancellationToken.None);

        restored.Should().BeEquivalentTo(new
        {
            Status = QueryVersionStatus.Active,
            VersionNumber = 3,
            ChangeSource = "Restore",
            ContentHash = old.ContentHash,
            CreatedByUserId = "ext-ada",
            CreatedByUserName = "Ada Lovelace",
        }, x => x.ExcludingMissingMembers());
        active.Status.Should().Be(QueryVersionStatus.Archived);
        store.Saved.OfType<QueryStep>().Should().ContainSingle().Which.SqlValue.Should().Be("SELECT 0");
        store.SaveCount.Should().Be(1);
    }

    // A restore with the workflow off leaves the live query with exactly the restored content: it hashes as the version,
    // with its steps (names, data sources, SQL, parameter types and placeholders), final query, name and description.
    [Test]
    public async Task Restoring_WithTheWorkflowOff_LeavesTheLiveQueryWithExactlyTheRestoredContent()
    {
        var store = new GovernanceStore();
        var steps = new List<QueryStepSnapshot>
        {
            new()
            {
                StepOrder = 1,
                SqlValue = "SELECT id FROM orders WHERE total > {minimum}",
                DataSourceId = GovernanceStore.DataSourceId,
                DataSourceName = "warehouse",
                Name = "Large orders",
                Parameters = [new QueryStepParameterSnapshot { Name = "minimum", Type = ParameterType.Number, Placeholder = "{minimum}" }],
            },
            new()
            {
                StepOrder = 2,
                SqlValue = "SELECT id FROM refunds",
                DataSourceId = GovernanceStore.LedgerId,
                DataSourceName = "ledger",
                Name = "Refunds",
            },
        };
        var old = store.AddVersion(1, QueryVersionStatus.Archived, finalQuery: "SELECT * FROM step1 JOIN step2 USING (id)");
        old.Name = "Large orders with refunds";
        old.Description = "Orders above a total, with their refunds";
        old.StepsJson = JsonSerializer.Serialize(steps, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        old.ContentHash = QueryContentHash.Of(old.FinalQuery, steps).Hash;
        store.Activate(store.AddVersion(2, QueryVersionStatus.Active));

        await TestQueryServices.VersionService(store.Factory()).RestoreVersionAsync(old.Id, "ext-ada", CancellationToken.None);

        var live = new Query
        {
            FinalQuery = store.Query.FinalQuery,
            Steps = store.Query.Steps.Concat(store.Saved.OfType<QueryStep>()).ToList(),
        };
        QueryContentHash.Of(live).Hash.Should().Be(old.ContentHash);
        store.Query.Should().BeEquivalentTo(new
        {
            Name = "Large orders with refunds",
            Description = "Orders above a total, with their refunds",
            FinalQuery = "SELECT * FROM step1 JOIN step2 USING (id)",
        }, x => x.ExcludingMissingMembers());
        live.Steps[0].Parameters.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Name = "minimum", Type = ParameterType.Number, Placeholder = "{minimum}" });
    }

    [Test]
    public async Task Restoring_WithTheWorkflowOn_IsSubmittedForApproval_AndChangesNothingLive()
    {
        var store = new GovernanceStore();
        var old = store.AddVersion(1, QueryVersionStatus.Archived, "SELECT 0");
        var active = store.Activate(store.AddVersion(2, QueryVersionStatus.Active));

        var proposal = (await TestQueryServices.VersionService(store.Factory(), configuration: WorkflowOn()).RestoreVersionAsync(old.Id, "ext-ada", CancellationToken.None)).Version;

        proposal.Status.Should().Be(QueryVersionStatus.PendingApproval);
        proposal.ContentHash.Should().Be(old.ContentHash);
        proposal.ChangeSource.Should().Be("Restore");
        store.Saved.OfType<QueryApprovalRequest>().Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            QueryVersion = proposal,
            Status = ApprovalStatus.Pending,
            RequestedByUserId = "ext-ada",
            BaseVersionId = (int?)active.Id,
            ChangeSummary = "Restore of version 1",
        }, x => x.ExcludingMissingMembers());
        active.Status.Should().Be(QueryVersionStatus.Active);
        store.Query.ActiveVersionId.Should().Be(active.Id);
        store.Query.Steps.Should().ContainSingle().Which.SqlValue.Should().Be("SELECT 1");
        store.Saved.OfType<QueryStep>().Should().BeEmpty();
    }

    // A restore is a submission like a save: it replaces the restorer's own pending request, and answers 409 while another
    // requester's change waits, which stays pending.
    [TestCase("ext-ben", true)]
    [TestCase("ext-ada", false)]
    public async Task Restoring_WithTheWorkflowOn_ReplacesOnlyTheRestorersOwnPendingRequest(string pendingRequester, bool refused)
    {
        var store = new GovernanceStore();
        var old = store.AddVersion(1, QueryVersionStatus.Archived, "SELECT 0");
        var active = store.Activate(store.AddVersion(2, QueryVersionStatus.Active));
        var waiting = store.AddRequest(store.AddVersion(3, QueryVersionStatus.PendingApproval, "SELECT 9"), pendingRequester, active.Id);

        var act = () => TestQueryServices.VersionService(store.Factory(), configuration: WorkflowOn()).RestoreVersionAsync(old.Id, "ext-ada", CancellationToken.None);

        if (refused)
        {
            await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(QueryVersionService.OtherPendingMessage);
            waiting.Status.Should().Be(ApprovalStatus.Pending);
            store.Saved.Should().BeEmpty();
            return;
        }

        (await act.Should().NotThrowAsync()).Subject.SupersededRequests.Should().Be(1);
        waiting.Status.Should().Be(ApprovalStatus.Superseded);
    }

    // History must hold the stored SQL a restore replaces: stored SQL no version holds (changed outside a versioned save)
    // is recorded as an Archived baseline first, with the workflow on (before the proposal) and off (before the restore).
    [TestCase(true)]
    [TestCase(false)]
    public async Task Restoring_OverStoredSqlNoVersionHolds_RecordsItFirst(bool workflowOn)
    {
        var store = new GovernanceStore();
        var old = store.AddVersion(1, QueryVersionStatus.Archived, "SELECT 0");
        store.Activate(store.AddVersion(2, QueryVersionStatus.Active));
        store.Query.Steps[0].SqlValue = "SELECT 2 AS drifted";
        var drifted = QueryContentHash.Of(store.Query).Hash;

        var restored = (await TestQueryServices.VersionService(store.Factory(), configuration: workflowOn ? WorkflowOn() : new BeaconConfiguration())
            .RestoreVersionAsync(old.Id, "ext-ada", CancellationToken.None)).Version;

        var baseline = store.Saved.OfType<QueryVersion>().Should().ContainSingle(x => x.ChangeSource == QueryVersionService.BaselineSource).Subject;
        baseline.Status.Should().Be(QueryVersionStatus.Archived);
        baseline.VersionNumber.Should().Be(3);
        baseline.ContentHash.Should().Be(drifted);
        baseline.StepsJson.Should().Contain("SELECT 2 AS drifted");
        restored.VersionNumber.Should().Be(4, "the restore follows the baseline");
    }

    [Test]
    public async Task Restoring_OverStoredSqlAVersionHolds_RecordsNoBaseline()
    {
        var store = new GovernanceStore();
        var old = store.AddVersion(1, QueryVersionStatus.Archived, "SELECT 0");
        store.Activate(store.AddVersion(2, QueryVersionStatus.Active));

        var restored = (await TestQueryServices.VersionService(store.Factory(), configuration: WorkflowOn()).RestoreVersionAsync(old.Id, "ext-ada", CancellationToken.None)).Version;

        store.Saved.OfType<QueryVersion>().Should().ContainSingle().Which.Should().BeSameAs(restored);
        restored.VersionNumber.Should().Be(3);
    }

    // Orders that do not run 1, 2, 3, … leave the run order or a review's step diff to chance: such a snapshot (written
    // before orders were checked) is not put back.
    [TestCase(1, 1)]
    [TestCase(1, 3)]
    public async Task Restoring_AVersionWhoseStepOrdersDoNotRunInSequence_IsRefused(int firstOrder, int secondOrder)
    {
        var store = new GovernanceStore();
        var old = store.AddVersion(1, QueryVersionStatus.Archived);
        old.StepsJson = JsonSerializer.Serialize(
            new List<QueryStepSnapshot> { GovernanceStore.Snapshot("SELECT 1", firstOrder), GovernanceStore.Snapshot("SELECT 2", secondOrder, "Step 2") },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        store.Activate(store.AddVersion(2, QueryVersionStatus.Active));

        var act = () => TestQueryServices.VersionService(store.Factory(), configuration: WorkflowOn()).RestoreVersionAsync(old.Id, "ext-ada", CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage($"*{QueryStepOrders.Refusal}");
        store.Saved.Should().BeEmpty();
    }

    [Test]
    public async Task Restoring_WithTheWorkflowOn_WithoutARequester_IsRefused()
    {
        var store = new GovernanceStore();
        var old = store.AddVersion(1, QueryVersionStatus.Archived, "SELECT 0");
        store.Activate(store.AddVersion(2, QueryVersionStatus.Active));

        var act = () => TestQueryServices.VersionService(store.Factory(), configuration: WorkflowOn()).RestoreVersionAsync(old.Id, null, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(QueryVersionService.RequesterRequiredMessage);
        store.Saved.Should().BeEmpty();
    }

    [Test]
    public async Task Restoring_AVersionWhoseSqlNoLongerPassesTheSaveCheck_IsRefused()
    {
        var store = new GovernanceStore();
        var old = store.AddVersion(1, QueryVersionStatus.Archived, "SELECT 0");
        store.Activate(store.AddVersion(2, QueryVersionStatus.Active));
        var saveCheck = new Mock<IStepSqlSaveCheck>();
        saveCheck
            .Setup(x => x.RejectionAsync(GovernanceStore.DataSourceId, "SELECT 0", It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Data source 3 not found.");

        var act = () => TestQueryServices.VersionService(store.Factory(), saveCheck: saveCheck.Object).RestoreVersionAsync(old.Id, "ext-ada", CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage("Step 1 cannot be restored: Data source 3 not found.");
        store.Saved.Should().BeEmpty();
    }

    // An Editor of the project (Write, not Manage): an unlocked query and their own lock are theirs to restore, another
    // user's lock is not.
    [TestCase(null, true)]
    [TestCase("ext-ada", true)]
    [TestCase("ext-ben", false)]
    public async Task Restoring_ALockedQuery_NeedsManageUnlessTheCallerHoldsTheLock(string? lockedBy, bool allowed)
    {
        var store = new GovernanceStore();
        var old = store.AddVersion(1, QueryVersionStatus.Archived, "SELECT 0");
        store.Query.IsLocked = lockedBy != null;
        store.Query.LockedByUserId = lockedBy;
        var versions = new Mock<IQueryVersionService>();
        versions
            .Setup(x => x.RestoreVersionAsync(old.Id, "ext-ada", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryVersionChange(new QueryVersion { Id = 500, QueryId = GovernanceStore.QueryId, VersionNumber = 2, Status = QueryVersionStatus.Active, Name = "Orders", StepsJson = "[]" }, 0));
        var editor = TestProjectAccess.Scope(new Dictionary<int, ProjectRole> { [1] = ProjectRole.Editor }, includesOpen: false, notOpen: []);
        var authorizer = TestProjectAccess.Authorizer(editor, new LockedQueryResolver(lockedBy), store.Factory());
        var handler = new RestoreQueryVersionHandler(versions.Object, store.Factory(), TestProjectAccess.Actors(), authorizer);

        var act = () => handler.Handle(new RestoreQueryVersionCommand { VersionId = old.Id }, CancellationToken.None);

        if (allowed)
        {
            (await act.Should().NotThrowAsync()).Subject.Should().Be(new RestoreQueryVersionResult(2, false, 0));
        }
        else
        {
            await act.Should().ThrowAsync<BeaconForbiddenException>();
            versions.VerifyNoOtherCalls();
        }
    }

    [Test]
    public async Task TheRestoreResult_SaysWhenTheRestoreWaitsForApproval()
    {
        var store = new GovernanceStore();
        var old = store.AddVersion(1, QueryVersionStatus.Archived, "SELECT 0");
        var versions = new Mock<IQueryVersionService>();
        versions
            .Setup(x => x.RestoreVersionAsync(old.Id, "ext-ada", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryVersionChange(new QueryVersion { Id = 500, QueryId = GovernanceStore.QueryId, VersionNumber = 4, Status = QueryVersionStatus.PendingApproval, Name = "Orders", StepsJson = "[]" }, 1));
        var handler = new RestoreQueryVersionHandler(versions.Object, store.Factory(), TestProjectAccess.Actors(), TestProjectAccess.Authorizer(TestProjectAccess.Unrestricted));

        var result = await handler.Handle(new RestoreQueryVersionCommand { VersionId = old.Id }, CancellationToken.None);

        result.Should().Be(new RestoreQueryVersionResult(4, true, 1), "it says how many pending requests the restore replaced");
    }

    private static BeaconConfiguration WorkflowOn() => new() { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true } };

    // The query lives in project 1; its lock holder is its recorded owner, as the real resolver reads it.
    private sealed class LockedQueryResolver(string? lockedBy) : IProjectResourceResolver
    {
        public Task<ResolvedProjectResource> ResolveAsync(BeaconContext context, ProjectResource resource, CancellationToken cancellationToken) =>
            Task.FromResult(ResolvedProjectResource.InProject(1) with { OwnerUserId = lockedBy });
    }
}

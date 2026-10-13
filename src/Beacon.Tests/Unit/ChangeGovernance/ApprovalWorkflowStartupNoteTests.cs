using System.Text.Json;
using Beacon.Api;
using Beacon.Core;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// The startup note on change governance: the workflow off is a Warning outside Development (Information in it), self
/// approval a Warning, and the run rule with the workflow on Information. With the workflow on, after startup it counts
/// the queries never versioned, whether fewer than two users can approve, and (outside strict mode) the MCP tools whose
/// approval recorded no content hash — a count that cannot be read is noted, never a failure. Configuration names, counts
/// and tool names only.
/// </summary>
[TestFixture]
public class ApprovalWorkflowStartupNoteTests
{
    [Test]
    public async Task TheCounts_WarnAboutWhatTheRulesLeaveOpen()
    {
        var store = new GovernanceStore();
        store.Query.McpToolEnabled = true;
        store.Query.McpToolName = "orders";
        var active = store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        store.AddRequest(active, "ext-ben", null, ApprovalStatus.Approved, reviewedBy: "ext-cid");
        store.Queries.Add(new Query { Id = 8, Name = "Never versioned", Steps = [new QueryStep { Id = 20, QueryId = 8, DataSourceId = GovernanceStore.DataSourceId, StepOrder = 1, SqlValue = "SELECT 1" }] });
        store.Queries.Add(new Query { Id = 9, Name = "Empty" });
        store.Users.Add(Approver(3, "ext-ada"));
        using var recorder = new LogRecorder();

        await Note(recorder, new ApprovalWorkflowOptions { Enabled = true }).NoteCountsAsync(Services(store), CancellationToken.None);

        var warnings = recorder.Entries.Where(x => x.Level == LogLevel.Warning).Select(x => x.Message).ToList();
        warnings.Should().HaveCount(3);
        warnings.Should().ContainSingle(x => x.StartsWith("1 queries hold stored SQL that no version records", StringComparison.Ordinal));
        warnings.Should().ContainSingle(x => x.StartsWith("1 active users can approve a query change", StringComparison.Ordinal));
        warnings.Should().ContainSingle(x => x.StartsWith("1 MCP tools run a version whose approval recorded no content hash", StringComparison.Ordinal))
            .Which.Should().Contain("q_orders");
        recorder.Entries.Select(x => x.Message).Should().NotContain(x => x.Contains("SELECT", StringComparison.Ordinal));
    }

    [Test]
    public async Task NothingLeftOpen_IsNotWarnedAbout()
    {
        var store = new GovernanceStore();
        store.Query.McpToolEnabled = true;
        store.Query.McpToolName = "orders";
        var active = store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        store.AddRequest(active, "ext-ben", null, ApprovalStatus.Approved, approvedContentHash: active.ContentHash, reviewedBy: "ext-cid");
        store.Users.AddRange([Approver(3, "ext-ada"), Approver(4, "ext-ben"), new BeaconUser { Id = 5, ExternalId = "ext-cid", UserName = "cid", IsEnabled = true, IsSuperAdmin = true }]);
        using var recorder = new LogRecorder();

        await Note(recorder, new ApprovalWorkflowOptions { Enabled = true }).NoteCountsAsync(Services(store), CancellationToken.None);

        recorder.Entries.Should().NotContain(x => x.Level == LogLevel.Warning);
    }

    // Under separation of duties a user holding both permissions of a conflicting pair holds neither; a disabled user
    // approves nothing; an Admin holds every permission.
    [Test]
    public async Task TheApproverCount_FollowsThePermissionsUsersHoldNow()
    {
        var store = new GovernanceStore();
        var conflicted = Approver(3, "ext-ada");
        conflicted.UserRoles.Add(new BeaconUserRole { Role = new BeaconRole { Name = "SecurityAdmin", IsSystemRole = true, Level = RoleService.RoleLevels.Functional } });
        var disabled = Approver(4, "ext-ben");
        disabled.IsEnabled = false;
        var admin = new BeaconUser { Id = 5, ExternalId = "ext-cid", UserName = "cid", IsEnabled = true };
        admin.UserRoles.Add(new BeaconUserRole { Role = new BeaconRole { Name = RoleService.RoleNames.Admin, IsSystemRole = true, Level = RoleService.RoleLevels.Admin } });
        store.Users.AddRange([conflicted, disabled, admin, Approver(6, "ext-dan")]);
        var separated = new RoleMappingOptions { SeparationOfDuties = new SeparationOfDutiesOptions { Enabled = true } };

        (await ApprovalWorkflowReport.CountApproversAsync(store.Factory(), separated, CancellationToken.None)).Should().Be(2);
        (await ApprovalWorkflowReport.CountApproversAsync(store.Factory(), new RoleMappingOptions(), CancellationToken.None)).Should().Be(3);
    }

    // In strict mode the tools on approvals without a hash do not run, and the never-versioned queries do not either: the
    // note says so instead of listing tools, and counts the active versions that need approving again.
    [Test]
    public async Task InStrictMode_TheToolsAreNotListed_AndWhatDoesNotRunIsCounted()
    {
        var store = new GovernanceStore();
        store.Query.McpToolEnabled = true;
        store.Query.McpToolName = "orders";
        var active = store.Activate(store.AddVersion(2, QueryVersionStatus.Active));
        store.AddRequest(active, "ext-ben", null, ApprovalStatus.Approved, reviewedBy: "ext-cid");
        store.Queries.Add(new Query { Id = 8, Name = "Never versioned", Steps = [new QueryStep { Id = 20, QueryId = 8, DataSourceId = GovernanceStore.DataSourceId, StepOrder = 1, SqlValue = "SELECT 1" }] });
        using var recorder = new LogRecorder();

        await Note(recorder, new ApprovalWorkflowOptions { Enabled = true, RequireApprovedVersionToRun = true, AllowSelfApproval = true }).NoteCountsAsync(Services(store), CancellationToken.None);

        var warnings = recorder.Entries.Where(x => x.Level == LogLevel.Warning).Select(x => x.Message).ToList();
        warnings.Should().HaveCount(2);
        warnings.Should().ContainSingle(x => x.Contains("strict mode runs none of them", StringComparison.Ordinal));
        warnings.Should().ContainSingle(x => x.StartsWith("1 queries with subscriptions or MCP tools have an active version that is not approved for its content hash", StringComparison.Ordinal))
            .Which.Should().Contain("restore it and approve the restore").And.NotContain("q_orders");
    }

    // A query whose stored SQL is not its active version's content has every run skipped: counted, with the remedy, when it
    // has a subscription; a query without one, or whose stored SQL is its active version's, is not.
    [Test]
    public async Task TheScheduledQueriesWhoseStoredSqlIsNotTheirActiveVersion_AreCounted()
    {
        var store = new GovernanceStore();
        store.Activate(store.AddVersion(1, QueryVersionStatus.Active, "SELECT 1"));
        store.Query.Steps[0].SqlValue = "SELECT 2";
        store.Query.Subscriptions.Add(new Subscription { Id = 41, QueryId = GovernanceStore.QueryId, CronExpression = "0 * * * *" });
        Versioned(store, 8, "SELECT 1", "SELECT 1", withSubscription: true);
        Versioned(store, 9, "SELECT 1", "SELECT 3", withSubscription: false);
        using var recorder = new LogRecorder();

        await Note(recorder, new ApprovalWorkflowOptions { Enabled = true, AllowSelfApproval = true }).NoteCountsAsync(Services(store), CancellationToken.None);

        (await ApprovalWorkflowReport.CountDriftedScheduledQueriesAsync(store.Factory(), CancellationToken.None)).Should().Be(1);
        recorder.Entries.Where(x => x.Level == LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().StartWith("1 queries with subscriptions hold stored SQL that is not their active version's content")
            .And.Contain("a save submits the stored SQL for approval");
        recorder.Entries.Select(x => x.Message).Should().NotContain(x => x.Contains("SELECT", StringComparison.Ordinal));
    }

    // Strict mode: an active version approved for exactly its content runs; one approved without a hash, for another hash,
    // or not approved, is counted when the query has a subscription or an MCP tool.
    [Test]
    public async Task InStrictMode_TheActiveVersionsNotApprovedForTheirHash_AreCounted()
    {
        var store = new GovernanceStore();
        var approved = store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        store.AddRequest(approved, "ext-ben", null, ApprovalStatus.Approved, approvedContentHash: approved.ContentHash, reviewedBy: "ext-cid");
        store.Query.Subscriptions.Add(new Subscription { Id = 41, QueryId = GovernanceStore.QueryId, CronExpression = "0 * * * *" });
        var otherHash = Versioned(store, 8, "SELECT 1", "SELECT 1", withSubscription: true);
        store.AddRequest(otherHash, "ext-ben", null, ApprovalStatus.Approved, approvedContentHash: new string('0', 64), reviewedBy: "ext-cid");
        Versioned(store, 9, "SELECT 1", "SELECT 1", withSubscription: false).Query.McpToolEnabled = true;
        Versioned(store, 10, "SELECT 1", "SELECT 1", withSubscription: false);

        var count = await ApprovalWorkflowReport.CountActiveVersionsNotApprovedForTheirHashAsync(store.Factory(), CancellationToken.None);

        count.Should().Be(2, "the version approved for another hash and the tool's unapproved version; the query with neither a subscription nor a tool is not counted");
    }

    [Test]
    public async Task ACountThatCannotBeRead_IsNoted_NotAFailure()
    {
        var failing = new Mock<IDbContextFactory<BeaconContext>>();
        failing.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("no database"));
        var services = new ServiceCollection().AddSingleton(failing.Object).BuildServiceProvider();
        using var recorder = new LogRecorder();

        var act = () => Note(recorder, new ApprovalWorkflowOptions { Enabled = true }).NoteCountsAsync(services, CancellationToken.None);

        await act.Should().NotThrowAsync();
        recorder.Entries.Should().HaveCount(4).And.OnlyContain(x => x.Level == LogLevel.Information && x.Message.Contains("could not be read", StringComparison.Ordinal));
    }

    // Another query (id) with one warehouse step running stepSql and an active version holding versionSql.
    private static QueryVersion Versioned(GovernanceStore store, int id, string versionSql, string stepSql, bool withSubscription)
    {
        var steps = new List<QueryStepSnapshot> { GovernanceStore.Snapshot(versionSql) };
        var version = new QueryVersion
        {
            Id = id * 100,
            QueryId = id,
            VersionNumber = 1,
            Status = QueryVersionStatus.Active,
            Name = "Other",
            StepsJson = JsonSerializer.Serialize(steps, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            ContentHash = QueryContentHash.Of(null, steps).Hash,
        };
        var query = new Query
        {
            Id = id,
            Name = "Other",
            ActiveVersionId = version.Id,
            ActiveVersion = version,
            Steps = [new QueryStep { Id = id * 10, QueryId = id, DataSourceId = GovernanceStore.DataSourceId, StepOrder = 1, Name = "Step 1", SqlValue = stepSql }],
        };
        if (withSubscription)
        {
            query.Subscriptions.Add(new Subscription { Id = id * 1000, QueryId = id, CronExpression = "0 * * * *" });
        }

        version.Query = query;
        store.Queries.Add(query);
        store.Versions.Add(version);

        return version;
    }

    // A host with Beacon user management, whose stored roles say who can approve.
    private static ApprovalWorkflowStartupNote Note(LogRecorder recorder, ApprovalWorkflowOptions workflow) =>
        new(
            recorder.For<ApprovalWorkflowStartupNote>(),
            new BeaconConfiguration { ApprovalWorkflow = workflow, UserManagement = new UserManagementOptions { Enabled = true } },
            null,
            Options.Create(new RoleMappingOptions()));

    private static ServiceProvider Services(GovernanceStore store) =>
        new ServiceCollection()
            .AddSingleton(store.Factory())
            .BuildServiceProvider();

    // An enabled user holding the Approver functional role.
    private static BeaconUser Approver(int id, string externalId)
    {
        var user = new BeaconUser { Id = id, ExternalId = externalId, UserName = externalId, IsEnabled = true };
        user.UserRoles.Add(new BeaconUserRole { Role = new BeaconRole { Name = "Approver", IsSystemRole = true, Level = RoleService.RoleLevels.Functional } });

        return user;
    }

    [TestCase(false, LogLevel.Warning)]
    [TestCase(true, LogLevel.Information)]
    public void TheWorkflowOff_IsNoted(bool isDevelopment, LogLevel level)
    {
        using var recorder = new LogRecorder();

        ApprovalWorkflowStartupNote.Note(recorder.For<ApprovalWorkflowStartupNote>(), new ApprovalWorkflowOptions(), isDevelopment);

        recorder.Entries.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Level = level }, x => x.ExcludingMissingMembers());
        recorder.Contains("approval workflow is off").Should().BeTrue();
    }

    [Test]
    public void SelfApproval_IsAWarning_AndTheRunRuleIsNoted()
    {
        using var recorder = new LogRecorder();

        ApprovalWorkflowStartupNote.Note(
            recorder.For<ApprovalWorkflowStartupNote>(),
            new ApprovalWorkflowOptions { Enabled = true, AllowSelfApproval = true, RequireApprovedVersionToRun = true },
            isDevelopment: false);

        recorder.Entries.Where(x => x.Level == LogLevel.Warning).Should().ContainSingle().Which.Message.Should().Contain("AllowSelfApproval");
        recorder.Contains("approved for exactly its content").Should().BeTrue();
    }

    // Outside strict mode a query never versioned runs as stored: the note says so instead of claiming every run is checked
    // against an active version.
    [Test]
    public void TheRunRuleOutsideStrictMode_SaysQueriesNeverVersionedRunAsStored()
    {
        using var recorder = new LogRecorder();

        ApprovalWorkflowStartupNote.Note(recorder.For<ApprovalWorkflowStartupNote>(), new ApprovalWorkflowOptions { Enabled = true }, isDevelopment: false);

        recorder.Contains("a query never versioned runs as stored, and each such run is logged").Should().BeTrue();
    }

    [Test]
    public void TheWorkflowOnWithoutSelfApproval_WarnsAboutNothing()
    {
        using var recorder = new LogRecorder();

        ApprovalWorkflowStartupNote.Note(recorder.For<ApprovalWorkflowStartupNote>(), new ApprovalWorkflowOptions { Enabled = true }, isDevelopment: false);

        recorder.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Information);
    }
}

using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Home;
using Beacon.Core.Handlers.Metadata;
using Beacon.Core.Handlers.Queries;
using Beacon.Core.Handlers.QueryFolders;
using Beacon.Core.Handlers.Subscriptions;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.ControlTower;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// Save-time and execute-time project checks (§8.1 S and X), and the list behaviours that are not a plain filter: a
/// query saved with steps no one project lets the caller write is refused; a folder holding queries the caller cannot
/// change is not deleted; a relationship must belong to the route's data source; previews and on-demand runs re-check
/// the sources right before the SQL (and the refusal is not turned into a "preview failed" result); approvals of legacy
/// versions are checked per row; Control Tower statistics are cached per scope; migration totals are an Admin's.
/// </summary>
[TestFixture]
public class ProjectSaveAndExecuteTimeTests
{
    /// <summary>Editor in project 2, Viewer in project 1, and the openly reachable sources except data source 9.</summary>
    private static readonly ProjectAccessScope Member = TestProjectAccess.Scope(
        new Dictionary<int, ProjectRole> { [1] = ProjectRole.Viewer, [2] = ProjectRole.Editor },
        includesOpen: true,
        notOpen: [9]);

    [Test]
    public void SavingAQuery_ChecksTheStepsItIsSavedWith_AsOneSet()
    {
        var command = new UpdateQueryCommand
        {
            QueryId = 5,
            Query = new QueryData
            {
                Name = "q",
                Steps =
                [
                    new QueryStepData { StepOrder = 1, DataSourceId = 4, SqlValue = "select 1" },
                    new QueryStepData { StepOrder = 2, DataSourceId = 9, SqlValue = "select 2" }
                ]
            }
        };

        var checks = ((IProjectAuthorizedRequest)command).ProjectChecks.ToList();

        checks.Should().HaveCount(2);
        checks[0].Resource.Kind.Should().Be(ProjectResourceKind.Query);
        checks[1].Resource.Kind.Should().Be(ProjectResourceKind.DataSources);
        checks[1].Resource.Ids.Should().Equal(4, 9);
        checks.Should().OnlyContain(x => x.Action == ProjectAction.Write);
    }

    [Test]
    public async Task StepsSpanningProjects_NoOneOfWhichHoldsThemAll_AreRefused()
    {
        // Data sources 4 and 9 exist; no project the caller may write holds both (the link lookup finds none).
        var capture = new SqlCapture().ThenScalar(2).ThenNoRows();
        var authorizer = TestProjectAccess.Authorizer(Member, factory: capture.Factory());

        var act = () => authorizer.DemandAsync(new ProjectCheck(ProjectResource.DataSources([4, 9]), ProjectAction.Write), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconNotFoundException>();
        capture.Commands[1].Should().Contain("project_data_sources").And.Contain("GROUP BY");
    }

    [Test]
    public async Task ADraftPreview_RunsTheDraftsSourcesUnderQuery_AndTheStoredQueryUnderRun()
    {
        var stored = ((IProjectAuthorizedRequest)new ExecuteQueryPreviewCommand { QueryId = 5 }).ProjectChecks.ToList();
        var draft = ((IProjectAuthorizedRequest)new ExecuteQueryPreviewCommand
        {
            QueryId = 5,
            Draft = new QueryDraft { Steps = [new QueryStepData { StepOrder = 1, DataSourceId = 4, SqlValue = "select 1" }] }
        }).ProjectChecks.ToList();

        stored.Should().ContainSingle().Which.Action.Should().Be(ProjectAction.Run);
        draft.Select(x => (x.Resource.Kind, x.Action)).Should().Equal(
            (ProjectResourceKind.Query, ProjectAction.Read),
            (ProjectResourceKind.DataSources, ProjectAction.Query));
        await Task.CompletedTask;
    }

    [Test]
    public async Task APreview_ReChecksTheSourcesRightBeforeRunning_AndTheRefusalIsNotAResult()
    {
        // The query's source was unlinked from the caller's projects after the request's own check: the re-check refuses.
        var queryService = new Mock<IQueryService>(MockBehavior.Strict);
        var stored = new RecordingStoredQueryPreview();
        var service = new QueryExecutionPreviewService(
            queryService.Object,
            stored,
            TestProjectAccess.Authorizer(Member, new FixedResolver(ResolvedProjectResource.InProject(3)), new SqlCapture().Factory()),
            NullLogger<QueryExecutionPreviewService>.Instance);

        var act = () => service.ExecuteQueryPreview(5, null, new Paging(), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconNotFoundException>();
        queryService.VerifyNoOtherCalls();
        stored.Loads.Should().BeEmpty();
        stored.Runs.Should().BeEmpty();
    }

    [Test]
    public async Task RunningASubscriptionNow_ReChecksItsQueryBeforeTheJob()
    {
        var jobs = new Mock<IJobService>();
        var capture = new SqlCapture().ThenNoRows();
        var handler = new TestSubscriptionHandler(
            jobs.Object,
            capture.Factory(),
            TestProjectAccess.Actors(),
            TestProjectAccess.Authorizer(Member, new FixedResolver(ResolvedProjectResource.InProject(1)), new SqlCapture().Factory()),
            NullLogger<TestSubscriptionHandler>.Instance);

        await handler.Handle(new TestSubscriptionCommand(42), CancellationToken.None);

        jobs.Verify(x => x.ExecuteQuery(42, It.IsAny<CancellationToken>()), Times.Once, "a Viewer may run the subscription's stored query");

        var denied = new TestSubscriptionHandler(
            jobs.Object,
            new SqlCapture().ThenNoRows().Factory(),
            TestProjectAccess.Actors(),
            TestProjectAccess.Authorizer(Member, new FixedResolver(ResolvedProjectResource.InProject(3)), new SqlCapture().Factory()),
            NullLogger<TestSubscriptionHandler>.Instance);
        var act = () => denied.Handle(new TestSubscriptionCommand(43), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconNotFoundException>();
        jobs.Verify(x => x.ExecuteQuery(43, It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestCase(true, typeof(BeaconForbiddenException))]
    [TestCase(false, typeof(InvalidOperationException))]
    public async Task DeletingAFolder_NeedsWriteOnEveryQueryItHolds(bool holdsAnUnwritableQuery, Type outcome)
    {
        // The first command asks whether the folder holds a query the caller cannot write; when it does not, the
        // handler goes on to load the folder (none here: not found).
        var capture = new SqlCapture().ThenScalar(holdsAnUnwritableQuery).ThenNoRows();
        var handler = new DeleteQueryFolderHandler(
            capture.Factory(),
            TestProjectAccess.Evaluator(Member),
            TestProjectAccess.Actors(),
            NullLogger<DeleteQueryFolderHandler>.Instance);

        var act = () => handler.Handle(new DeleteQueryFolderCommand(3, true), CancellationToken.None);

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeOfType(outcome);
        capture.Commands[0].Should().Contain("folder_id").And.Contain("NOT");
    }

    [Test]
    public async Task ARelationshipOfAnotherDataSource_IsNotFound()
    {
        var capture = new SqlCapture().ThenNoRows();
        var handler = new UpdateSchemaRelationshipHandler(capture.Factory(), Mock.Of<ISchemaGraphService>());

        var act = () => handler.Handle(new UpdateSchemaRelationshipCommand { DataSourceId = 4, Id = 70, Label = "l" }, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconNotFoundException>();
        capture.Commands.Should().ContainSingle().Which.Should().Contain("data_source_id = @");
    }

    [Test]
    public async Task PendingApprovals_OfALegacyVersionReadingAnUnreadableSource_AreLeftOutBeforePaging()
    {
        var created = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var capture = new SqlCapture()
            .ThenRows([10, """[{"stepOrder":1,"sqlValue":"select 1","dataSourceId":9,"dataSourceName":"d"}]"""])
            .ThenNoRows()
            .ThenScalar(1)
            .ThenRows([11, 5, "q", 2, (int)ApprovalStatus.Pending, "ada", created, "recorded"]);
        var service = new QueryApprovalService(capture.Factory(), Mock.Of<IQueryVersionService>());

        var page = await service.GetPendingApprovalsAsync(new Paging(), null, Member.For(ProjectAction.Read), CancellationToken.None);

        page.Items.Select(x => x.Id).Should().Equal(11);
        page.TotalCount.Should().Be(1, "the count leaves out the same unreadable legacy request as the page");
        capture.Commands[0].Should().Contain("data_source_ids IS NULL");
        capture.Commands[1].Should().StartWith("SELECT").And.Contain("project_data_sources", "the links of every legacy request are read once");
        capture.Commands[2].Should().Contain("project_data_sources", "the recorded sources and the live query are filtered in the SQL");
        ((IEnumerable<int>)capture.CommandParameters[2]["@unreadableLegacy"]!).Should().Equal([10], "the unreadable legacy request is excluded in the count");
        ((IEnumerable<int>)capture.CommandParameters[3]["@unreadableLegacy"]!).Should().Equal([10], "and in the page");
    }

    [Test]
    public async Task PendingApprovals_DecideEveryLegacyRequest_WithOneReadOfTheLinks()
    {
        var created = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var capture = new SqlCapture()
            .ThenRows(
                [10, """[{"stepOrder":1,"sqlValue":"select 1","dataSourceId":9,"dataSourceName":"d"}]"""],
                [12, """[{"stepOrder":1,"sqlValue":"select 1","dataSourceId":9,"dataSourceName":"d"},{"stepOrder":2,"sqlValue":"select 2","dataSourceId":8,"dataSourceName":"e"}]"""],
                [13, """[{"stepOrder":1,"sqlValue":"select 1","dataSourceId":7,"dataSourceName":"f"}]"""],
                [14, "not json"])
            .ThenRows([2, 9], [2, 8], [1, 9])
            .ThenScalar(3)
            .ThenRows(
                [12, 5, "q", 2, (int)ApprovalStatus.Pending, "ada", created, "two sources, one project"],
                [13, 6, "r", 1, (int)ApprovalStatus.Pending, "ada", created, "openly reachable"],
                [10, 5, "q", 1, (int)ApprovalStatus.Pending, "ada", created, "held by a project"]);
        var service = new QueryApprovalService(capture.Factory(), Mock.Of<IQueryVersionService>());

        var page = await service.GetPendingApprovalsAsync(new Paging(), null, Member.For(ProjectAction.Read), CancellationToken.None);

        page.TotalCount.Should().Be(3);
        capture.Commands.Should().HaveCount(4, "the legacy snapshots, their links once, then the count and the page");
        ((IEnumerable<int>)capture.CommandParameters[2]["@unreadableLegacy"]!).Should().Equal([14], "only the unreadable snapshot is left out: 10 and 12 are held by project 2, 13 is openly reachable");
        ((IEnumerable<int>)capture.CommandParameters[1]["@pending"]!).Should().BeEquivalentTo([9, 8], "the links are read once, for the sources not openly reachable");
    }

    [Test]
    public async Task ControlTowerStatistics_AreCachedPerScope()
    {
        var capture = new SqlCapture().AnsweringCountsWithOne();
        var service = new ControlTowerService(capture.Factory(), new MemoryCache(new MemoryCacheOptions()));
        var all = new GetControlTowerDataRequest { Readable = ProjectSet.All };
        var restricted = new GetControlTowerDataRequest { Readable = Member.For(ProjectAction.Read) };

        await service.GetControlTowerStatistics(all, CancellationToken.None);
        var afterFirst = capture.Commands.Count;
        await service.GetControlTowerStatistics(all, CancellationToken.None);
        var afterRepeat = capture.Commands.Count;
        await service.GetControlTowerStatistics(restricted, CancellationToken.None);

        afterRepeat.Should().Be(afterFirst, "the same scope is served from the cache");
        capture.Commands.Count.Should().BeGreaterThan(afterRepeat, "another scope never reads the first one's entry");
        capture.Commands.Last().Should().Contain("project_data_sources");
    }

    [Test]
    public async Task MigrationTotals_AreAnAdminsAlone()
    {
        var capture = new SqlCapture();
        var handler = new GetHomeMigrationSummaryHandler(capture.Factory(), TestProjectAccess.Evaluator(TestProjectAccess.FastPath));

        var summary = await handler.Handle(new GetHomeMigrationSummaryQuery(), CancellationToken.None);

        summary.Should().Be(new GetHomeMigrationSummaryResult(0, 0, 0, 0));
        capture.Commands.Should().BeEmpty();
    }

    private sealed record Paging : ListRequest;

    private sealed class FixedResolver(ResolvedProjectResource resolved) : IProjectResourceResolver
    {
        public Task<ResolvedProjectResource> ResolveAsync(BeaconContext context, ProjectResource resource, CancellationToken cancellationToken) =>
            Task.FromResult(resolved);
    }
}

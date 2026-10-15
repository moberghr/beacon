using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Handlers.Approvals;
using Beacon.Core.Handlers.Home;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// The duty-specific grants functional permissions carry beside project access: an Approver reads every approval request
/// (one that is missing is still a 404); a DataSourceAdmin changes a shared data source's state with Write where it acts,
/// without Write in every project linking the source; a MigrationOperator sees the Home migration totals. Without the
/// permission, project access decides as before.
/// </summary>
[TestFixture]
public class FunctionalPermissionGrantTests
{
    [Test]
    public async Task AnApprover_ReadsAnApprovalRequestOfAProjectItCannotRead()
    {
        var check = ProjectCheck.OrPermission(ProjectResource.ApprovalRequest(7), ProjectAction.Read, BeaconPermissions.Approver);
        var asApprover = Authorizer(ResolvedProjectResource.InProject(5), TestPermissions.Holding(BeaconPermissions.Approver));
        var asAnyoneElse = Authorizer(ResolvedProjectResource.InProject(5), TestPermissions.Holding(BeaconPermissions.Auditor));

        var approver = () => asApprover.DemandAsync(check, CancellationToken.None);
        var other = () => asAnyoneElse.DemandAsync(check, CancellationToken.None);

        await approver.Should().NotThrowAsync();
        await other.Should().ThrowAsync<BeaconNotFoundException>();
    }

    [Test]
    public async Task AnApprover_StillGetsNotFound_ForAMissingApprovalRequest()
    {
        var check = ProjectCheck.OrPermission(ProjectResource.ApprovalRequest(7), ProjectAction.Read, BeaconPermissions.Approver);

        var act = () => Authorizer(ResolvedProjectResource.Missing, TestPermissions.Holding(BeaconPermissions.Approver)).DemandAsync(check, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconNotFoundException>();
    }

    [Test]
    public void OnlyApprovalReadsAndMembership_AreGrantedByAPermission()
    {
        var granted = BeaconApiRoutes.RequestTypes()
            .Where(x => typeof(IProjectAuthorizedRequest).IsAssignableFrom(x))
            .SelectMany(x => ((IProjectAuthorizedRequest)RuntimeHelpers.GetUninitializedObject(x)).ProjectChecks
                .Where(y => y.GrantedBy != null)
                .Select(y => $"{x.Name}: {y.Action} by {y.GrantedBy}"))
            .ToList();

        granted.Should().BeEquivalentTo(
            "GetApprovalDetailQuery: Read by Approver",
            "GetProjectMembersQuery: ManageMembers by UserAdmin",
            "SearchProjectMemberCandidatesQuery: ManageMembers by UserAdmin",
            "SetProjectMemberCommand: ManageMembers by UserAdmin",
            "RemoveProjectMemberCommand: ManageMembers by UserAdmin");
    }

    [Test]
    public async Task AnApprover_ListsEveryProjectsPendingApprovals_AndAnyoneElseTheirOwnProjects()
    {
        var service = new Mock<IQueryApprovalService>();
        service
            .Setup(x => x.GetPendingApprovalsAsync(It.IsAny<ListRequest>(), It.IsAny<int?>(), It.IsAny<ProjectSet>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedList<ApprovalRequestSummary> { Items = [], PageCount = 0, TotalCount = 0 });
        var member = TestProjectAccess.ReadableIn(1);

        await new GetPendingApprovalsHandler(service.Object, TestProjectAccess.Evaluator(member), TestPermissions.Holding(BeaconPermissions.Approver))
            .Handle(new GetPendingApprovalsQuery(), CancellationToken.None);
        await new GetPendingApprovalsHandler(service.Object, TestProjectAccess.Evaluator(member), TestPermissions.None)
            .Handle(new GetPendingApprovalsQuery(), CancellationToken.None);

        service.Verify(x => x.GetPendingApprovalsAsync(It.IsAny<ListRequest>(), null, It.Is<ProjectSet>(y => y.Ids == null), It.IsAny<CancellationToken>()), Times.Once);
        service.Verify(x => x.GetPendingApprovalsAsync(It.IsAny<ListRequest>(), null, It.Is<ProjectSet>(y => y.Ids != null && y.Ids.SequenceEqual(new[] { 1 })), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ADataSourceAdmin_ChangesASharedSource_WithoutWriteInEveryProjectLinkingIt()
    {
        // The caller writes in project 1; project 2 links the same data source.
        var check = ProjectCheck.SharedSource(ProjectResource.DataSource(10), ProjectAction.Write);
        var scope = TestProjectAccess.Scope(new Dictionary<int, ProjectRole> { [1] = ProjectRole.Editor }, includesOpen: false, notOpen: []);

        var asDataSourceAdmin = () => SharedSourceAuthorizer(scope, TestPermissions.Holding(BeaconPermissions.DataSourceAdmin)).DemandAsync(check, CancellationToken.None);
        var asEditor = () => SharedSourceAuthorizer(scope, TestPermissions.None).DemandAsync(check, CancellationToken.None);

        await asDataSourceAdmin.Should().NotThrowAsync();
        await asEditor.Should().ThrowAsync<BeaconForbiddenException>().WithMessage("*every project that links it*");
    }

    [Test]
    public async Task ADataSourceAdmin_StillNeedsWriteWhereItActs()
    {
        var check = ProjectCheck.SharedSource(ProjectResource.DataSource(10), ProjectAction.Write);
        var viewer = TestProjectAccess.Scope(new Dictionary<int, ProjectRole> { [1] = ProjectRole.Viewer }, includesOpen: false, notOpen: []);

        var act = () => SharedSourceAuthorizer(viewer, TestPermissions.Holding(BeaconPermissions.DataSourceAdmin)).DemandAsync(check, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
    }

    [Test]
    public async Task AMigrationOperator_SeesTheHomeMigrationTotals()
    {
        var capture = new SqlCapture().AnsweringCountsWithOne();
        var handler = new GetHomeMigrationSummaryHandler(
            capture.Factory(),
            TestProjectAccess.Evaluator(TestProjectAccess.FastPath),
            TestPermissions.Holding(BeaconPermissions.MigrationOperator));

        var summary = await handler.Handle(new GetHomeMigrationSummaryQuery(), CancellationToken.None);

        summary.Should().Be(new GetHomeMigrationSummaryResult(1, 1, 1, 1));
        capture.Commands.Should().HaveCount(4);
    }

    private static ProjectRequestAuthorizer Authorizer(ResolvedProjectResource resolved, IBeaconPermissionAccessor permissions)
    {
        return TestProjectAccess.Authorizer(
            ProjectAccessScope.None,
            new FixedResolver(resolved),
            new SqlCapture().Factory(),
            permissions: permissions);
    }

    private static ProjectRequestAuthorizer SharedSourceAuthorizer(ProjectAccessScope scope, IBeaconPermissionAccessor permissions)
    {
        var ours = new Project { Id = 1, Name = "ours" };
        var theirs = new Project { Id = 2, Name = "theirs" };
        var links = new List<ProjectDataSource>
        {
            new() { ProjectId = 1, Project = ours, DataSourceId = 10 },
            new() { ProjectId = 2, Project = theirs, DataSourceId = 10 }
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object> { [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(links, []) },
                []));

        return TestProjectAccess.Authorizer(
            scope,
            new FixedResolver(ResolvedProjectResource.InProject(1, [10])),
            factory.Object,
            permissions: permissions);
    }

    private sealed class FixedResolver(ResolvedProjectResource resolved) : IProjectResourceResolver
    {
        public Task<ResolvedProjectResource> ResolveAsync(BeaconContext context, ProjectResource resource, CancellationToken cancellationToken) =>
            Task.FromResult(resolved);
    }
}

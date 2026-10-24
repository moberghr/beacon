using Beacon.Core.Authorization;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.SavedQueryTools;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Embed;

/// <summary>
/// The project access an embed grant (and a token issued for it) gets: its one project, read and run only, at most a
/// Viewer, through its creator's memberships and never with the Admin bypass or the openly reachable sources; nothing for
/// a creator without an active stored user.
/// </summary>
[TestFixture]
public class EmbedAccessProfileTests
{
    [Test]
    public async Task AGrant_ReadsAndRunsInItsOneProject_Only()
    {
        var data = Seed();

        var scope = await Scope(data, Embed(isAdmin: true));

        scope.IsUnrestricted.Should().BeFalse("an embed grant never has the Admin bypass");
        scope.Allows(1, ProjectAction.Read).Should().BeTrue();
        scope.Allows(1, ProjectAction.Run).Should().BeTrue();
        scope.Allows(1, ProjectAction.Query).Should().BeFalse();
        scope.Allows(1, ProjectAction.Write).Should().BeFalse();
        scope.Allows(2, ProjectAction.Read).Should().BeFalse("only the grant's project");
        scope.For(ProjectAction.Read).IncludesOpen.Should().BeFalse("never the openly reachable sources outside it");
    }

    [Test]
    public async Task InARestrictedProject_TheCreatorsMembershipDecides_AtMostAsAViewer()
    {
        var data = Seed(requiresMembership: true);

        (await Scope(data, Embed(isAdmin: true))).Allows(1, ProjectAction.Run).Should().BeFalse("an Admin who is no member");
        data.Store.ListFor<ProjectMember>().Add(new ProjectMember { ProjectId = 1, UserId = 3, Role = ProjectRole.ProjectAdmin });
        var member = await Scope(data, Embed(isAdmin: false));
        member.Allows(1, ProjectAction.Run).Should().BeTrue();
        member.Allows(1, ProjectAction.Manage).Should().BeFalse("a ProjectAdmin's grant still only reads and runs");
        member.RoleIn(1).Should().Be(ProjectRole.Viewer);
    }

    [Test]
    public async Task ACreatorWithoutAnActiveStoredUser_OrAGrantWithoutOneProject_GetsNothing()
    {
        var data = Seed();

        (await Scope(data, Embed() with { IsActiveUser = false })).Allows(1, ProjectAction.Read).Should().BeFalse();
        (await Scope(data, Embed() with { AllowedProjectIds = [1, 2] })).Allows(1, ProjectAction.Read).Should().BeFalse();
        (await Scope(data, Embed() with { AllowedProjectIds = null })).Allows(1, ProjectAction.Read).Should().BeFalse();
    }

    private static SavedQueryTestData Seed(bool requiresMembership = false)
    {
        var data = new SavedQueryTestData();
        data.Project(1, "A", data.DataSource(10, "warehouse")).RequiresMembership = requiresMembership;
        data.Project(2, "B", data.DataSource(11, "ledger"));

        return data;
    }

    private static BeaconActor Embed(bool isAdmin = false) =>
        new("ext-ada", isAdmin)
        {
            Kind = BeaconPrincipalKind.Embed,
            BeaconUserId = 3,
            IsActiveUser = true,
            IsLiveAdmin = isAdmin,
            RoleLevel = isAdmin ? RoleService.RoleLevels.Admin : RoleService.RoleLevels.Editor,
            AllowedProjectIds = [1]
        };

    private static Task<ProjectAccessScope> Scope(SavedQueryTestData data, BeaconActor actor)
    {
        var factory = data.Store.Factory().Object;

        return TestProjectAccess.RealEvaluator(BeaconActor.Nobody, factory).GetScopeAsync(actor, CancellationToken.None);
    }
}

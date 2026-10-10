using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Mcp;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// The API key dialog's project picker: an Admin reads every project, but a key of theirs reaches only what they read
/// without the Admin bypass (here the open project 1 and the restricted project 2 they are a member of), so the list for a
/// key names only those, while the caller's own role is still reported.
/// </summary>
[TestFixture]
public class ApiKeyProjectPickerTests
{
    private static readonly ProjectAccessScope AsOwnKey = TestProjectAccess.Scope(
        new Dictionary<int, ProjectRole> { [1] = ProjectRole.Viewer, [2] = ProjectRole.Viewer },
        includesOpen: false,
        notOpen: []);

    [Test]
    public async Task ForAnApiKey_TheListHoldsOnlyTheProjectsReadWithoutTheAdminBypass()
    {
        var page = await Handler().Handle(new GetProjectsQuery { ForApiKey = true }, CancellationToken.None);

        page.Items.Select(x => x.Id).Should().BeEquivalentTo([1, 2]);
        page.TotalCount.Should().Be(2);
        page.Items.Should().OnlyContain(x => x.MyRole == ProjectRole.ProjectAdmin, "the caller's own role is reported, not the key's");
    }

    [Test]
    public async Task Otherwise_AnAdminListsEveryProject()
    {
        var page = await Handler().Handle(new GetProjectsQuery(), CancellationToken.None);

        page.Items.Select(x => x.Id).Should().BeEquivalentTo([1, 2, 3]);
    }

    private static GetProjectsHandler Handler()
    {
        var actors = TestProjectAccess.Actors();
        var evaluator = new Mock<IProjectAccessEvaluator>();
        evaluator
            .Setup(x => x.GetScopeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProjectAccessScope.Unrestricted);
        evaluator
            .Setup(x => x.GetScopeAsync(
                It.Is<BeaconActor>(y => y.Kind == BeaconPrincipalKind.ApiKey && y.ScopeCeiling == McpCallerScope.Read && y.AllowedProjectIds == null && y.UserId == "ext-ada"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AsOwnKey);

        return new GetProjectsHandler(Factory(), evaluator.Object, actors, Options.Create(new ProjectAuthorizationOptions()));
    }

    private static IDbContextFactory<BeaconContext> Factory()
    {
        var projects = new List<Project>
        {
            new() { Id = 1, Name = "Open" },
            new() { Id = 2, Name = "Restricted, a member", RequiresMembership = true },
            new() { Id = 3, Name = "Restricted", RequiresMembership = true }
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object> { [typeof(Project)] = RecordingBeaconContext.MemorySet(projects, []) },
                []));

        return factory.Object;
    }
}

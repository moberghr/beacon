using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Models;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// A resource addressed by its data sources is reachable through one project of the caller's set that holds every one
/// of them, or through the openly reachable sources when every one of them is; never by combining two projects.
/// Project 1 holds sources 10 and 11, project 2 holds 20; 20 is not openly reachable.
/// </summary>
[TestFixture]
public class ProjectSourceFootprintTests
{
    [TestCase(new[] { 10, 11 }, true)]
    [TestCase(new[] { 10, 20 }, false)]
    [TestCase(new[] { 20 }, true)]
    [TestCase(new int[0], false)]
    public async Task ThroughProjects_OneProjectMustHoldEverySource(int[] sources, bool allowed)
    {
        var result = await Allows(new ProjectSet([1, 2], false, []), sources);

        result.Should().Be(allowed);
    }

    [TestCase(new[] { 10, 40 }, true)]
    [TestCase(new[] { 20, 40 }, false)]
    [TestCase(new int[0], false)]
    public async Task ThroughOpenSources_EverySourceMustBeOpen_AndNoSourceIsNotOpen(int[] sources, bool allowed)
    {
        var result = await Allows(new ProjectSet([], true, [20]), sources);

        result.Should().Be(allowed);
    }

    [Test]
    public async Task EverySource_IsReachableWithinTheWholeSet()
    {
        (await Allows(ProjectSet.All, [20, 99])).Should().BeTrue();
    }

    [TestCase(new[] { 10, 11 }, new[] { 1 })]
    [TestCase(new[] { 10 }, new[] { 1, 3 })]
    [TestCase(new[] { 20 }, new[] { 2 })]
    [TestCase(new[] { 10, 20 }, new int[0])]
    [TestCase(new[] { 10, 11, 99 }, new int[0])]
    public void TheHolders_AreTheProjectsLinkingEveryOneOfTheSources(int[] sources, int[] holders)
    {
        using var context = Context();

        ProjectSourceFootprint.HoldersOf(context, sources).ToList().Should().BeEquivalentTo(holders);
    }

    [Test]
    public async Task TheAuthorizer_AllowsThroughOneHoldingProject_NeverByCombiningTwo()
    {
        var memberOfBoth = TestProjectAccess.Scope(
            new Dictionary<int, ProjectRole> { [1] = ProjectRole.Viewer, [2] = ProjectRole.Viewer },
            includesOpen: false,
            notOpen: []);
        var memberOfTwo = TestProjectAccess.Scope(new Dictionary<int, ProjectRole> { [2] = ProjectRole.Viewer }, includesOpen: false, notOpen: []);

        await Authorizer(memberOfBoth, [10, 11]).DemandAsync(Read, CancellationToken.None);
        var combined = () => Authorizer(memberOfBoth, [10, 20]).DemandAsync(Read, CancellationToken.None);
        var notHeld = () => Authorizer(memberOfTwo, [10, 11]).DemandAsync(Read, CancellationToken.None);

        await combined.Should().ThrowAsync<BeaconNotFoundException>("no one project of the caller's holds both sources");
        await notHeld.Should().ThrowAsync<BeaconNotFoundException>();
    }

    private static ProjectCheck Read => new(ProjectResource.Query(7), ProjectAction.Read);

    private static async Task<bool> Allows(ProjectSet set, int[] sources)
    {
        await using var context = Context();

        return await ProjectSourceFootprint.AllowsAsync(context, set, sources, CancellationToken.None);
    }

    private static ProjectRequestAuthorizer Authorizer(ProjectAccessScope scope, IReadOnlyList<int> sources)
    {
        var resolver = new FixedResolver(ResolvedProjectResource.ThroughQuery(sources, "ext-owner"));
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Context());

        return TestProjectAccess.Authorizer(scope, resolver, factory.Object);
    }

    private sealed class FixedResolver(ResolvedProjectResource resolved) : IProjectResourceResolver
    {
        public Task<ResolvedProjectResource> ResolveAsync(BeaconContext context, ProjectResource resource, CancellationToken cancellationToken)
        {
            return Task.FromResult(resolved);
        }
    }

    // Project 3 is archived: its links still hold their sources; whether it counts is the caller's set's to decide.
    private static RecordingBeaconContext Context()
    {
        return new RecordingBeaconContext(
            new Dictionary<Type, object>
            {
                [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(new List<ProjectDataSource>
                {
                    new() { ProjectId = 1, DataSourceId = 10 },
                    new() { ProjectId = 1, DataSourceId = 11 },
                    new() { ProjectId = 2, DataSourceId = 20 },
                    new() { ProjectId = 3, DataSourceId = 10 }
                }, [])
            },
            []);
    }
}

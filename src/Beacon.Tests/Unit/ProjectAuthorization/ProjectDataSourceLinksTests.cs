using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.HostData;
using Beacon.Core.Models;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// Linking data sources into a project: a non-Admin links only sources a live open project already holds, so a
/// link never carries a restricted project's data elsewhere; a source the caller cannot read answers 404 like a missing
/// one, a readable but unlinkable one 403; a host's source links only into the project the host declares for it (the
/// same host key), for Admins too.
/// </summary>
[TestFixture]
public class ProjectDataSourceLinksTests
{
    private const int OpenProject = 1;
    private const int RestrictedProject = 2;

    private const string HostSourceKey = "efcore:ShopContext";
    private const string HostProjectKey = "host:shop";

    private static readonly BeaconActor Caller = new("ext-ana", false);

    // The host declares ShopContext for its "Shop" project.
    private static readonly Func<string, string?> HostProjectKeys = x => x == HostSourceKey ? HostProjectKey : null;

    [Test]
    public void AnEditor_LinksASourceAnOpenProjectHolds()
    {
        var act = () => Check(Editor(), Candidate(10, links: [new SourceLink(OpenProject, true)]));

        act.Should().NotThrow();
    }

    [Test]
    public void AnEditor_CannotLinkASourceOnlyARestrictedProjectHolds_EvenAsItsMember()
    {
        var memberOfRestricted = TestProjectAccess.Scope(
            new Dictionary<int, ProjectRole> { [RestrictedProject] = ProjectRole.ProjectAdmin },
            includesOpen: true,
            notOpen: [20]);

        var act = () => Check(memberOfRestricted, Candidate(20, links: [new SourceLink(RestrictedProject, false)]));

        act.Should().Throw<BeaconForbiddenException>().WithMessage("*no open project holds*");
    }

    [Test]
    public void ASourceOnlyARestrictedProjectTheCallerCannotReadHolds_IsNotFound()
    {
        var act = () => Check(Editor(notOpen: [20]), Candidate(20, links: [new SourceLink(RestrictedProject, false)]));

        act.Should().Throw<BeaconNotFoundException>();
    }

    [Test]
    public void AnEditor_CannotLinkAnUnlinkedSource_ThatIsStillOpen()
    {
        var act = () => Check(Editor(), Candidate(40, openWhenUnlinked: true, links: []));

        act.Should().Throw<BeaconForbiddenException>();
    }

    [Test]
    public void AnUnlinkedClosedSource_IsNotFoundForANonAdmin()
    {
        var act = () => Check(Editor(notOpen: [50]), Candidate(50, openWhenUnlinked: false, links: []));

        act.Should().Throw<BeaconNotFoundException>();
    }

    [Test]
    public void ASourceLinkedOnlyToAnArchivedProject_IsNeverOpen()
    {
        var act = () => Check(Editor(notOpen: [30]), Candidate(30, openWhenUnlinked: true, links: [new SourceLink(3, false)]));

        act.Should().Throw<BeaconNotFoundException>("an archived project's link counts as a link, and opens nothing");
    }

    [Test]
    public void AMissingSource_IsNotFound_ForAnAdminToo()
    {
        var act = () => ProjectDataSourceLinks.Check(
            TestProjectAccess.Unrestricted,
            [99],
            [],
            targetHostKey: null,
            HostProjectKeys,
            requireMembershipEverywhere: false,
            Caller,
            NullLogger.Instance);

        act.Should().Throw<BeaconNotFoundException>();
    }

    [Test]
    public void AnAdmin_LinksRestrictedOnlyAndUnlinkedSources()
    {
        var restrictedOnly = () => Check(TestProjectAccess.Unrestricted, Candidate(20, links: [new SourceLink(RestrictedProject, false)]));
        var unlinked = () => Check(TestProjectAccess.Unrestricted, Candidate(50, openWhenUnlinked: false, links: []));

        restrictedOnly.Should().NotThrow();
        unlinked.Should().NotThrow();
    }

    [Test]
    public void AHostsSource_LinksOnlyIntoTheProjectTheHostDeclaresForIt_ForAnAdminToo()
    {
        var hostSource = Candidate(70, links: [new SourceLink(5, true)], hostManagedKey: HostSourceKey);
        var undeclared = Candidate(71, links: [new SourceLink(5, true)], hostManagedKey: "efcore:RemovedContext");

        var intoBeaconProject = () => Check(TestProjectAccess.Unrestricted, hostSource);
        var intoOtherHostProject = () => Check(TestProjectAccess.Unrestricted, hostSource, targetHostKey: "host:billing");
        var intoItsHostProject = () => Check(TestProjectAccess.Unrestricted, hostSource, targetHostKey: HostProjectKey);
        var undeclaredSource = () => Check(TestProjectAccess.Unrestricted, undeclared, targetHostKey: HostProjectKey);

        intoBeaconProject.Should().Throw<BeaconForbiddenException>().WithMessage("*host*");
        intoOtherHostProject.Should().Throw<BeaconForbiddenException>("another host project is not the one this source was declared for");
        intoItsHostProject.Should().NotThrow();
        undeclaredSource.Should().Throw<BeaconForbiddenException>("no host project is declared for a source the host no longer registers");
    }

    [Test]
    public void AnOrdinarySource_LinksIntoAHostProject()
    {
        var act = () => Check(Editor(), Candidate(10, links: [new SourceLink(OpenProject, true)]), targetHostKey: HostProjectKey);

        act.Should().NotThrow();
    }

    [Test]
    public void TheHostProjectKeys_ComeFromTheRegistrationsProjectName()
    {
        var registry = new TestHostDataSources(
            new HostDataSourceRegistration(typeof(ShopContext), new HostDbContextOptions { ProjectName = " Shop ", ReadOnlyConnectionStringName = "ShopReadOnly" }));

        var keys = ProjectDataSourceLinks.HostProjectKeys(registry);

        keys(HostSourceKey).Should().Be(HostProjectKey);
        keys("efcore:Other").Should().BeNull();
    }

    [Test]
    public void WhenEveryProjectRequiresMembership_OnlyAnAdminLinks()
    {
        var candidate = Candidate(10, links: [new SourceLink(OpenProject, true)]);
        var member = TestProjectAccess.Scope(
            new Dictionary<int, ProjectRole> { [OpenProject] = ProjectRole.ProjectAdmin },
            includesOpen: false,
            notOpen: []);

        var act = () => ProjectDataSourceLinks.Check(member, [10], [candidate], null, HostProjectKeys, requireMembershipEverywhere: true, Caller, NullLogger.Instance);

        act.Should().Throw<BeaconForbiddenException>();
    }

    private static void Check(ProjectAccessScope scope, LinkCandidate candidate, string? targetHostKey = null)
    {
        ProjectDataSourceLinks.Check(
            scope,
            [candidate.DataSourceId],
            [candidate],
            targetHostKey,
            HostProjectKeys,
            requireMembershipEverywhere: false,
            Caller,
            NullLogger.Instance);
    }

    // An Editor of the open project who reaches the openly reachable sources (all but notOpen).
    private static ProjectAccessScope Editor(IReadOnlyList<int>? notOpen = null)
    {
        return TestProjectAccess.Scope(
            new Dictionary<int, ProjectRole> { [OpenProject] = ProjectRole.Editor },
            includesOpen: true,
            notOpen: notOpen ?? []);
    }

    private static LinkCandidate Candidate(int id, IReadOnlyList<SourceLink> links, bool openWhenUnlinked = false, string? hostManagedKey = null)
    {
        return new LinkCandidate(id, hostManagedKey, openWhenUnlinked, links);
    }

    private sealed class ShopContext;
}

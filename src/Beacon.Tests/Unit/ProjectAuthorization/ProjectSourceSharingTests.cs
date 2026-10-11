using System.Data;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Handlers.Projects.Members;
using Beacon.Core.Models;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// Who else reaches a project's data sources, and taking a source out of a project: an Admin sees every other live
/// project (open or restricted) that links one of the project's sources, with its ProjectAdmins by display name, in the
/// project detail and in the answer to restricting or opening it; only an Admin unlinks a source, and a source whose last
/// link goes is left reachable by Admins only, never unlinked and open.
/// </summary>
[TestFixture]
public class ProjectSourceSharingTests
{
    private const int ProjectId = 1;

    [Test]
    public async Task UnlinkingASourcesLastLink_ClosesIt_AndRemovesTheLink()
    {
        var source = Source(10, openWhenUnlinked: true);
        var links = new List<ProjectDataSource> { Link(ProjectId, source) };
        var transactions = new SqlCapture();
        var logs = new LogRecorder();

        var result = await Unlink(Factory(links: links, offline: transactions), TestProjectAccess.Unrestricted, logs)
            .Handle(new UnlinkProjectDataSourceCommand(ProjectId, 10), CancellationToken.None);

        result.Should().Be(new UnlinkProjectDataSourceResult(ProjectId, 10, true));
        links.Should().BeEmpty();
        source.OpenWhenUnlinked.Should().BeFalse("an unlinked source is reachable by Admins only");
        transactions.TransactionIsolationLevels.Should().Equal(IsolationLevel.Serializable);
        transactions.Commits.Should().Be(1);
        logs.Entries.Should().ContainSingle(x => x.Message.StartsWith("Data source unlinked", StringComparison.Ordinal))
            .Which.Message.Should().Contain("project 1").And.Contain("data source 10").And.Contain("left unlinked True").And.Contain("user 3");
    }

    [Test]
    public async Task UnlinkingASourceAnotherProjectStillLinks_LeavesItsOpenFlag()
    {
        var source = Source(10, openWhenUnlinked: true);
        var links = new List<ProjectDataSource> { Link(ProjectId, source), Link(2, source) };

        var result = await Unlink(Factory(links: links, offline: new SqlCapture()), TestProjectAccess.Unrestricted)
            .Handle(new UnlinkProjectDataSourceCommand(ProjectId, 10), CancellationToken.None);

        result.LeftUnlinked.Should().BeFalse();
        links.Should().ContainSingle().Which.ProjectId.Should().Be(2);
        source.OpenWhenUnlinked.Should().BeTrue();
    }

    [Test]
    public async Task Unlinking_IsAnAdminsAlone_AndNeedsAnExistingLink_OfAnOrdinarySource()
    {
        var hostSource = Source(70, hostManagedKey: "efcore:ShopContext");
        var links = new List<ProjectDataSource> { Link(ProjectId, Source(10)), Link(ProjectId, hostSource) };
        var projectAdmin = TestProjectAccess.Scope(new Dictionary<int, ProjectRole> { [ProjectId] = ProjectRole.ProjectAdmin }, includesOpen: true, notOpen: []);

        var byProjectAdmin = () => Unlink(Factory(links: links, offline: new SqlCapture()), projectAdmin).Handle(new UnlinkProjectDataSourceCommand(ProjectId, 10), CancellationToken.None);
        var notLinked = () => Unlink(Factory(links: links, offline: new SqlCapture()), TestProjectAccess.Unrestricted).Handle(new UnlinkProjectDataSourceCommand(ProjectId, 11), CancellationToken.None);
        var hosts = () => Unlink(Factory(links: links, offline: new SqlCapture()), TestProjectAccess.Unrestricted).Handle(new UnlinkProjectDataSourceCommand(ProjectId, 70), CancellationToken.None);

        await byProjectAdmin.Should().ThrowAsync<BeaconForbiddenException>();
        await notLinked.Should().ThrowAsync<BeaconNotFoundException>();
        await hosts.Should().ThrowAsync<BeaconConflictException>().WithMessage("*host*");
        links.Should().HaveCount(2);
    }

    [Test]
    public async Task Unlinking_WhileTheLinksChangeConcurrently_IsAConflict()
    {
        var capture = new SqlCapture().ThenThrow(new PostgresException("conflict", "ERROR", "ERROR", "40001"));

        var act = () => Unlink(capture.Factory(), TestProjectAccess.Unrestricted).Handle(new UnlinkProjectDataSourceCommand(ProjectId, 10), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>();
        capture.TransactionIsolationLevels.Should().Equal(IsolationLevel.Serializable);
        capture.Commits.Should().Be(0);
    }

    [Test]
    public async Task AnAdmin_SeesEveryOtherProjectLinkingTheProjectsSources_WithTheirProjectAdmins()
    {
        var ten = Source(10);
        var eleven = Source(11);
        var target = new Project { Id = ProjectId, Name = "Finance" };
        var restricted = new Project { Id = 2, Name = "Audit", RequiresMembership = true };
        var open = new Project { Id = 3, Name = "Analytics" };
        var unrelated = new Project { Id = 4, Name = "Marketing" };
        Links(target, ten, eleven);
        Links(restricted, ten);
        Links(open, eleven);
        Links(unrelated, Source(12));
        restricted.Members =
        [
            Member(restricted, 5, ProjectRole.ProjectAdmin, "Bea Auditor"),
            Member(restricted, 6, ProjectRole.ProjectAdmin, "Disabled Admin", enabled: false),
            Member(restricted, 7, ProjectRole.Editor, "Ed Itor")
        ];
        open.Members = [Member(open, 9, ProjectRole.ProjectAdmin, null)];
        var projects = new List<Project> { target, restricted, open, unrelated };

        using var context = Context(projects);
        var shared = await SharedSourceProjects.ReadAsync(context, ProjectId, requireMembershipEverywhere: false, CancellationToken.None);

        shared.Should().BeEquivalentTo(
            [
                new SharedSourceProjectEntry(3, "Analytics", false, [11], ["User 9"]),
                new SharedSourceProjectEntry(2, "Audit", true, [10], ["Bea Auditor"])
            ],
            x => x.WithStrictOrdering());
    }

    [Test]
    public async Task SharedSourceProjects_AreReadInOneCommand_OfLiveProjects()
    {
        var capture = new SqlCapture().ThenNoRows();
        await using var context = await capture.Factory().CreateDbContextAsync();

        var shared = await SharedSourceProjects.ReadAsync(context, ProjectId, requireMembershipEverywhere: true, CancellationToken.None);

        shared.Should().BeEmpty();
        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("archived_time IS NULL", "an archived project reaches nothing");
        sql.Should().Contain("display_name").And.NotContain("user_name").And.NotContain("email");
    }

    [Test]
    public async Task OnlyAnAdmin_SeesTheProjectsSharingTheSources_InTheProjectDetail()
    {
        var admin = DetailRows().ThenNoRows().ThenNoRows();
        var projectAdmin = DetailRows().ThenNoRows();
        var projectAdminScope = TestProjectAccess.Scope(new Dictionary<int, ProjectRole> { [ProjectId] = ProjectRole.ProjectAdmin }, includesOpen: true, notOpen: []);

        await Detail(admin, TestProjectAccess.Unrestricted).Handle(new GetProjectDetailQuery(ProjectId), CancellationToken.None);
        var forProjectAdmin = (await Detail(projectAdmin, projectAdminScope).Handle(new GetProjectDetailQuery(ProjectId), CancellationToken.None)).Project!;

        admin.Commands.Should().HaveCount(6);
        admin.Commands.Last().Should().Contain("project_members", "the other projects' ProjectAdmins are read for an Admin");
        forProjectAdmin.SharedSourceProjects.Should().BeEmpty("another project's name and admins are an Admin's to see");
        projectAdmin.Commands.Should().HaveCount(5, "nothing is read about other projects for a ProjectAdmin");
    }

    [Test]
    public async Task RestrictingAProject_AnswersWithTheProjectsThatStillReachItsSources()
    {
        var ten = Source(10);
        var target = new Project { Id = ProjectId, Name = "Finance" };
        var other = new Project { Id = 2, Name = "Audit" };
        Links(target, ten);
        Links(other, ten);
        other.Members = [Member(other, 5, ProjectRole.ProjectAdmin, "Bea Auditor")];
        var projects = new List<Project> { target, other };
        var configuration = new BeaconConfiguration();
        configuration.EnableUserManagement();
        var factory = Factory(projects: projects, links: projects.SelectMany(x => x.DataSources).ToList());

        var result = await new SetProjectAccessModeHandler(
                factory,
                TestProjectAccess.Evaluator(TestProjectAccess.Unrestricted),
                new DatabaseProjectMembershipSource(configuration, factory),
                TestProjectAccess.Actors(),
                TestPermissions.None,
                Options.Create(new ProjectAuthorizationOptions()),
                NullLogger<SetProjectAccessModeHandler>.Instance)
            .Handle(new SetProjectAccessModeCommand(ProjectId, true), CancellationToken.None);

        target.RequiresMembership.Should().BeTrue();
        result.SharedSourceProjects.Should().ContainSingle().Which.Should().BeEquivalentTo(new SharedSourceProjectEntry(2, "Audit", false, [10], ["Bea Auditor"]));
    }

    // The project row (restricted), no data sources, no repositories, no documentation.
    private static SqlCapture DetailRows()
    {
        return new SqlCapture()
            .ThenRow(ProjectId, "Finance", "desc", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), true)
            .ThenNoRows()
            .ThenNoRows()
            .ThenScalar(false);
    }

    private static UnlinkProjectDataSourceHandler Unlink(IDbContextFactory<BeaconContext> factory, ProjectAccessScope scope, LogRecorder? logs = null)
    {
        return new UnlinkProjectDataSourceHandler(
            factory,
            TestProjectAccess.Evaluator(scope),
            TestProjectAccess.Actors(),
            logs?.For<UnlinkProjectDataSourceHandler>() ?? NullLogger<UnlinkProjectDataSourceHandler>.Instance);
    }

    private static GetProjectDetailHandler Detail(SqlCapture capture, ProjectAccessScope scope)
    {
        var configuration = new BeaconConfiguration();
        configuration.EnableUserManagement();

        return new GetProjectDetailHandler(
            capture.Factory(),
            TestProjectAccess.Evaluator(scope),
            new DatabaseProjectMembershipSource(configuration, capture.Factory()),
            SettingsProviderMock.Create().Object,
            Options.Create(new ProjectAuthorizationOptions()));
    }

    private static RecordingBeaconContext Context(List<Project> projects)
    {
        return new RecordingBeaconContext(
            new Dictionary<Type, object>
            {
                [typeof(Project)] = RecordingBeaconContext.MemorySet(projects, []),
                [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(projects.SelectMany(x => x.DataSources).ToList(), [])
            },
            [],
            new SqlCapture());
    }

    private static IDbContextFactory<BeaconContext> Factory(
        List<Project>? projects = null,
        List<ProjectDataSource>? links = null,
        SqlCapture? offline = null)
    {
        var saved = new List<object>();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object>
                {
                    [typeof(Project)] = RecordingBeaconContext.MemorySet(projects ?? [], saved),
                    [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(links ?? [], saved)
                },
                saved,
                offline ?? new SqlCapture()));

        return factory.Object;
    }

    private static void Links(Project project, params DataSource[] sources)
    {
        project.DataSources = sources
            .Select(x => new ProjectDataSource { ProjectId = project.Id, Project = project, DataSourceId = x.Id, DataSource = x })
            .ToList();
    }

    private static ProjectDataSource Link(int projectId, DataSource source)
    {
        return new ProjectDataSource { ProjectId = projectId, DataSourceId = source.Id, DataSource = source };
    }

    private static ProjectMember Member(Project project, int userId, ProjectRole role, string? displayName, bool enabled = true)
    {
        return new ProjectMember
        {
            ProjectId = project.Id,
            UserId = userId,
            Role = role,
            User = new BeaconUser { Id = userId, ExternalId = $"ext-{userId}", UserName = $"login{userId}", DisplayName = displayName, IsEnabled = enabled }
        };
    }

    private static DataSource Source(int id, bool openWhenUnlinked = false, string? hostManagedKey = null)
    {
        return new DataSource
        {
            Id = id,
            Name = $"source {id}",
            DataSourceType = DataSourceType.Database,
            EncryptedConnectionData = "enc",
            OpenWhenUnlinked = openWhenUnlinked,
            HostManagedKey = hostManagedKey
        };
    }
}

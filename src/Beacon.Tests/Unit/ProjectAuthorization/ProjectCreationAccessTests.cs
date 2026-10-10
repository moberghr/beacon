using System.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.DataSources;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.DataSources;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// Creating projects and data sources under project access: a new project is open and its creator, when a Beacon
/// user, is its ProjectAdmin; the data sources it links pass the link rules in a serializable transaction; a new data
/// source is reachable by Admins only until linked, and <c>projectIds</c> links it into projects the caller manages.
/// </summary>
[TestFixture]
public class ProjectCreationAccessTests
{
    private const int OpenProject = 4;
    private const int RestrictedProject = 2;

    [Test]
    public async Task ANewProject_IsOpen_AndItsCreatorIsItsProjectAdmin()
    {
        var saved = new List<object>();

        await CreateProject(saved, TestProjectAccess.Unrestricted).Handle(new CreateProjectCommand("p", null, [], []), CancellationToken.None);

        var project = saved.OfType<Project>().Should().ContainSingle().Subject;
        project.RequiresMembership.Should().BeFalse();
        project.Members.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            UserId = 3,
            Role = ProjectRole.ProjectAdmin,
            AddedByUserId = (int?)3
        });
    }

    [Test]
    public async Task ANewProject_LinksASourceAnOpenProjectHolds_AndRecordsWhoLinkedIt()
    {
        var saved = new List<object>();
        var transactions = new SqlCapture();

        await CreateProject(saved, Editor(), transactions).Handle(new CreateProjectCommand("p", null, [10, 10], []), CancellationToken.None);

        saved.OfType<Project>().Should().ContainSingle().Which.DataSources.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { DataSourceId = 10, LinkedByUserId = (int?)3 }, x => x.ExcludingMissingMembers());
        transactions.TransactionIsolationLevels.Should().Equal(IsolationLevel.Serializable);
    }

    [Test]
    public async Task ANewProject_WithoutDataSourcesOrRepositories_IsCreated()
    {
        var saved = new List<object>();
        var transactions = new SqlCapture();

        var result = await CreateProject(saved, Editor(), transactions).Handle(new CreateProjectCommand("p", null, null, null), CancellationToken.None);

        var project = saved.OfType<Project>().Should().ContainSingle().Subject;
        project.DataSources.Should().BeEmpty();
        project.Repositories.Should().BeEmpty();
        result.ProjectId.Should().Be(project.Id);
        transactions.TransactionIsolationLevels.Should().BeEmpty("no data source is linked, so nothing needs the serializable read");
    }

    [Test]
    public async Task ANewProject_WhoseSourcesChangeConcurrently_IsAConflict()
    {
        var capture = new SqlCapture()
            .ThenRows([10, DBNull.Value, false])
            .ThenRows([10, OpenProject, true])
            .ThenThrow(new Npgsql.PostgresException("conflict", "ERROR", "ERROR", "40001"));
        var encryption = new Mock<IEncryptionService>();
        var handler = new CreateProjectHandler(
            capture.Factory(),
            encryption.Object,
            TestProjectAccess.Actors(),
            TestProjectAccess.Evaluator(Editor()),
            Options.Create(new ProjectAuthorizationOptions()),
            NullLogger<CreateProjectHandler>.Instance);

        var act = () => handler.Handle(new CreateProjectCommand("p", null, [10], []), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage("*retry*");
        capture.TransactionIsolationLevels.Should().Contain(IsolationLevel.Serializable);
        capture.Commits.Should().Be(0);
    }

    [Test]
    public async Task ANewProject_ByANonAdmin_CannotLinkASourceOnlyARestrictedProjectHolds()
    {
        var saved = new List<object>();
        var memberOfRestricted = TestProjectAccess.Scope(
            new Dictionary<int, ProjectRole> { [RestrictedProject] = ProjectRole.ProjectAdmin, [OpenProject] = ProjectRole.Editor },
            includesOpen: true,
            notOpen: [20]);

        var act = () => CreateProject(saved, memberOfRestricted, new SqlCapture()).Handle(new CreateProjectCommand("p", null, [10, 20], []), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        saved.Should().BeEmpty();
    }

    [Test]
    public async Task ANewProject_CannotLinkAHostsSource_EvenByAnAdmin()
    {
        var saved = new List<object>();

        var act = () => CreateProject(saved, TestProjectAccess.Unrestricted, new SqlCapture()).Handle(new CreateProjectCommand("p", null, [70], []), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage("*host*");
        saved.Should().BeEmpty();
    }

    [Test]
    public async Task ANewDataSource_IsClosedUntilLinked_AndIsLinkedIntoTheGivenProjects()
    {
        var saved = new List<object>();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(new Dictionary<Type, object>(), saved));
        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(x => x.Encrypt(It.IsAny<string>())).Returns("enc");
        var service = new DataSourceService(
            factory.Object,
            encryption.Object,
            Mock.Of<IDataSourceProviderFactory>(),
            Mock.Of<IManualQueryExecutionLogger>(),
            Mock.Of<IBeaconUserContext>(),
            NullLogger<DataSourceService>.Instance);

        await service.CreateDataSource(
            new DataSourceData { Name = "warehouse", ConnectionString = "Host=x", DatabaseEngineType = DatabaseEngineType.PostgreSQL, ProjectIds = [OpenProject, OpenProject], LinkedByUserId = 3 },
            CancellationToken.None);

        var dataSource = saved.OfType<DataSource>().Should().ContainSingle().Subject;
        dataSource.OpenWhenUnlinked.Should().BeFalse("a new data source is reachable by Admins only until a project links it");
        saved.OfType<ProjectDataSource>().Should().ContainSingle().Which.Should().BeEquivalentTo(new { ProjectId = OpenProject, LinkedByUserId = (int?)3 }, x => x.ExcludingMissingMembers());
        saved.OfType<ProjectDataSource>().Single().DataSource.Should().BeSameAs(dataSource);
    }

    [Test]
    public async Task CreateDataSource_LinksOnlyIntoLiveProjectsTheCallerManages()
    {
        var service = new Mock<IDataSourceService>();
        service
            .Setup(x => x.CreateDataSource(It.IsAny<DataSourceData>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BaseResponse { Success = true });
        var projects = Factory([], projects: [new Project { Id = OpenProject, Name = "open" }]);
        var editor = TestProjectAccess.Scope(new Dictionary<int, ProjectRole> { [OpenProject] = ProjectRole.Editor }, includesOpen: true, notOpen: []);

        var missing = () => CreateDataSource(service.Object, projects, TestProjectAccess.Unrestricted).Handle(Command([9]), CancellationToken.None);
        var notManaged = () => CreateDataSource(service.Object, projects, editor).Handle(Command([OpenProject]), CancellationToken.None);
        await CreateDataSource(service.Object, projects, TestProjectAccess.Unrestricted).Handle(Command([OpenProject, OpenProject]), CancellationToken.None);

        await missing.Should().ThrowAsync<BeaconNotFoundException>();
        await notManaged.Should().ThrowAsync<BeaconForbiddenException>();
        service.Verify(
            x => x.CreateDataSource(It.Is<DataSourceData>(y => y.ProjectIds.SequenceEqual(new[] { OpenProject }) && y.LinkedByUserId == 3), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static CreateDataSourceCommand Command(List<int> projectIds)
    {
        return new CreateDataSourceCommand("warehouse", DataSourceType.Database, DatabaseEngineType.PostgreSQL, "Host=x", true, 0, 0, false, null, null, projectIds);
    }

    private static CreateDataSourceHandler CreateDataSource(IDataSourceService service, IDbContextFactory<BeaconContext> factory, ProjectAccessScope scope)
    {
        return new CreateDataSourceHandler(
            service,
            factory,
            TestProjectAccess.Evaluator(scope),
            TestProjectAccess.Actors(),
            NullLogger<CreateDataSourceHandler>.Instance);
    }

    private static CreateProjectHandler CreateProject(List<object> saved, ProjectAccessScope scope, SqlCapture? transactions = null)
    {
        var restricted = new Project { Id = RestrictedProject, Name = "restricted", RequiresMembership = true };
        var open = new Project { Id = OpenProject, Name = "open" };
        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(x => x.Encrypt(It.IsAny<string>())).Returns("enc");

        return new CreateProjectHandler(
            Factory(
                saved,
                dataSources:
                [
                    new DataSource { Id = 10, Name = "a", DataSourceType = DataSourceType.Database, EncryptedConnectionData = "enc" },
                    new DataSource { Id = 20, Name = "b", DataSourceType = DataSourceType.Database, EncryptedConnectionData = "enc" },
                    new DataSource { Id = 70, Name = "host", DataSourceType = DataSourceType.Database, EncryptedConnectionData = "enc", HostManagedKey = "host:db" }
                ],
                links:
                [
                    new ProjectDataSource { ProjectId = OpenProject, DataSourceId = 10, Project = open },
                    new ProjectDataSource { ProjectId = RestrictedProject, DataSourceId = 20, Project = restricted }
                ],
                offline: transactions),
            encryption.Object,
            TestProjectAccess.Actors(),
            TestProjectAccess.Evaluator(scope),
            Options.Create(new ProjectAuthorizationOptions()),
            NullLogger<CreateProjectHandler>.Instance);
    }

    private static IDbContextFactory<BeaconContext> Factory(
        List<object> saved,
        List<Project>? projects = null,
        List<DataSource>? dataSources = null,
        List<ProjectDataSource>? links = null,
        SqlCapture? offline = null)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object>
                {
                    [typeof(Project)] = RecordingBeaconContext.MemorySet(projects ?? [], saved),
                    [typeof(DataSource)] = RecordingBeaconContext.MemorySet(dataSources ?? [], saved),
                    [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(links ?? [], saved)
                },
                saved,
                offline));

        return factory.Object;
    }

    private static ProjectAccessScope Editor()
    {
        return TestProjectAccess.Scope(new Dictionary<int, ProjectRole> { [OpenProject] = ProjectRole.Editor }, includesOpen: true, notOpen: []);
    }
}

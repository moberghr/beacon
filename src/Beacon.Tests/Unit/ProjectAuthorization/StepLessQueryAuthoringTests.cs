using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Queries;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// Authoring a query while membership is required everywhere, through the real evaluator, resolver and list filter: a
/// new query has no steps, so no project holds it; its creator owns it, reads, lists and saves it, while a colleague in
/// the same project does not see it until it has steps. From then on the ordinary rule applies to everyone, its owner
/// included. Project 1 links data source 10 and project 2 links 20; the creator (user 7) and the colleague (user 8) are
/// Editors in project 1 only.
/// </summary>
[TestFixture]
public class StepLessQueryAuthoringTests
{
    private const string Creator = "ext-creator";
    private const int CreatorId = 7;
    private const string Colleague = "ext-colleague";
    private const int ColleagueId = 8;
    private const int QueryId = 5;

    private List<Project> _projects = null!;
    private List<DataSource> _dataSources = null!;
    private List<ProjectDataSource> _links = null!;
    private List<ProjectMember> _members = null!;
    private List<Query> _queries = null!;
    private List<object> _saved = null!;

    private static ProjectCheck Read => new(ProjectResource.Query(QueryId), ProjectAction.Read);

    [SetUp]
    public void SetUp()
    {
        _projects =
        [
            new Project { Id = 1, Name = "one" },
            new Project { Id = 2, Name = "two" }
        ];
        _dataSources = [Source(10), Source(20)];
        _links = [Link(1, 10), Link(2, 20)];
        _members =
        [
            new ProjectMember { ProjectId = 1, UserId = CreatorId, Role = ProjectRole.Editor },
            new ProjectMember { ProjectId = 1, UserId = ColleagueId, Role = ProjectRole.Editor }
        ];
        _queries = [];
        _saved = [];
    }

    [Test]
    public async Task TheCreator_ReadsListsAndSavesItsNewQuery_AndAColleagueSeesItOnlyOnceItHasSteps()
    {
        await new CreateQueryHandler(Factory(), Actors(Creator, CreatorId)).Handle(new CreateQueryCommand { Name = "Draft" }, CancellationToken.None);
        var query = _saved.OfType<Query>().Should().ContainSingle().Subject;
        query.OwnerUserId.Should().Be(Creator, "the creator is recorded as the owner, never a request field");
        query.Id = QueryId;
        _queries.Add(query);

        await Authorizer(Creator, CreatorId).DemandAsync(Read, CancellationToken.None);
        var colleagueReads = () => Authorizer(Colleague, ColleagueId).DemandAsync(Read, CancellationToken.None);
        await colleagueReads.Should().ThrowAsync<BeaconNotFoundException>("a query without steps is its owner's");
        (await Listed(Creator, CreatorId)).Should().Equal(QueryId);
        (await Listed(Colleague, ColleagueId)).Should().BeEmpty();

        await Authorizer(Creator, CreatorId).DemandAsync(SaveWithStepsOn(10), CancellationToken.None);
        var savedElsewhere = () => Authorizer(Creator, CreatorId).DemandAsync(SaveWithStepsOn(20), CancellationToken.None);
        await savedElsewhere.Should().ThrowAsync<BeaconNotFoundException>("project 2 is not the creator's");

        query.Steps.Add(new QueryStep { QueryId = QueryId, DataSourceId = 10, StepOrder = 1, Name = "Step 1", SqlValue = "SELECT 1" });

        await Authorizer(Colleague, ColleagueId).DemandAsync(Read, CancellationToken.None);
        (await Listed(Colleague, ColleagueId)).Should().Equal(QueryId);
    }

    [Test]
    public async Task OnceAQueryHasSteps_ItsOwnerGetsNoMoreThanItsMembershipGives()
    {
        _members.RemoveAll(x => x.UserId == CreatorId);
        _queries.Add(WithStepOn(10, Creator));

        var act = () => Authorizer(Creator, CreatorId).DemandAsync(Read, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconNotFoundException>();
        (await Listed(Creator, CreatorId)).Should().BeEmpty();
    }

    [Test]
    public async Task AQueryWithoutStepsOrOwner_IsAnAdminsAlone()
    {
        _queries.Add(new Query { Id = QueryId, Name = "recorded before owners" });

        var act = () => Authorizer(Creator, CreatorId).DemandAsync(Read, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconNotFoundException>();
        (await Listed(Creator, CreatorId)).Should().BeEmpty();
    }

    private static IEnumerable<ProjectCheck> SaveWithStepsOn(int dataSourceId)
    {
        return
        [
            new ProjectCheck(ProjectResource.Query(QueryId), ProjectAction.Write),
            new ProjectCheck(ProjectResource.DataSources([dataSourceId]), ProjectAction.Write)
        ];
    }

    private async Task<List<int>> Listed(string externalId, int userId)
    {
        var readable = (await Evaluator(Actors(externalId, userId)).GetScopeAsync(CancellationToken.None)).For(ProjectAction.Read);
        await using var context = Context();

        return context.Queries
            .WhereAllSourcesInOneProject(context, readable, x => x.Steps.Select(y => y.DataSourceId), x => x.OwnerUserId)
            .Select(x => x.Id)
            .ToList();
    }

    private ProjectRequestAuthorizer Authorizer(string externalId, int userId)
    {
        var actors = Actors(externalId, userId);

        return new ProjectRequestAuthorizer(
            Evaluator(actors),
            new ProjectResourceResolver(),
            Factory(),
            actors,
            NullLogger<ProjectRequestAuthorizer>.Instance);
    }

    private ProjectAccessEvaluator Evaluator(IBeaconActorAccessor actors)
    {
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.Enabled = true;
        configuration.Authorization.Enabled = true;
        var provider = new Mock<IBeaconAuthorizationProvider>();
        provider.Setup(x => x.HasWritePermissionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        provider.Setup(x => x.HasReadPermissionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var services = new Mock<IServiceProvider>();
        services.Setup(x => x.GetService(typeof(IBeaconAuthorizationProvider))).Returns(provider.Object);

        return new ProjectAccessEvaluator(
            actors,
            Factory(),
            new DatabaseProjectMembershipSource(configuration, Factory()),
            new DatabaseRunAsCeilingSource(),
            services.Object,
            configuration,
            Options.Create(new ProjectAuthorizationOptions { RequireProjectMembership = true }),
            NullLogger<ProjectAccessEvaluator>.Instance);
    }

    private static IBeaconActorAccessor Actors(string externalId, int userId)
    {
        return TestProjectAccess.Actors(userId, externalId);
    }

    private IDbContextFactory<BeaconContext> Factory()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Context());

        return factory.Object;
    }

    private RecordingBeaconContext Context()
    {
        return new RecordingBeaconContext(
            new Dictionary<Type, object>
            {
                [typeof(Project)] = RecordingBeaconContext.MemorySet(_projects, _saved),
                [typeof(DataSource)] = RecordingBeaconContext.MemorySet(_dataSources, _saved),
                [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(_links, _saved),
                [typeof(ProjectMember)] = RecordingBeaconContext.MemorySet(_members, _saved),
                [typeof(Query)] = RecordingBeaconContext.MemorySet(_queries, _saved)
            },
            _saved);
    }

    private static Query WithStepOn(int dataSourceId, string ownerUserId)
    {
        return new Query
        {
            Id = QueryId,
            Name = "saved",
            OwnerUserId = ownerUserId,
            Steps = [new QueryStep { QueryId = QueryId, DataSourceId = dataSourceId, StepOrder = 1, Name = "Step 1", SqlValue = "SELECT 1" }]
        };
    }

    private ProjectDataSource Link(int projectId, int dataSourceId)
    {
        return new ProjectDataSource
        {
            ProjectId = projectId,
            DataSourceId = dataSourceId,
            Project = _projects.First(x => x.Id == projectId)
        };
    }

    private static DataSource Source(int id)
    {
        return new DataSource
        {
            Id = id,
            Name = $"source {id}",
            DataSourceType = DataSourceType.Database,
            EncryptedConnectionData = "enc",
            OpenWhenUnlinked = false
        };
    }
}

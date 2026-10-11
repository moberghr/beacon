using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Handlers.Subscriptions;
using Beacon.Core.Models;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// The project authorization behaviour and its authorizer: an unreadable or missing resource is 404 before the handler,
/// a refused action 403 (logged with ids); Admins and the fast path read nothing; recorded owners manage where they
/// may write; a legacy run never reaches a caller confined to a project list; shared data-source state needs Write in
/// every project linking the source; a signal without a project is an Admin's; jobs without a request are not stopped.
/// </summary>
[TestFixture]
public class ProjectAuthorizationBehaviorTests
{
    private const int ReadableProject = 1;
    private const int WritableProject = 2;
    private const int OtherProject = 3;

    /// <summary>Viewer in project 1, Editor in project 2, and the openly reachable sources except data source 9.</summary>
    private static readonly ProjectAccessScope Member = TestProjectAccess.Scope(
        new Dictionary<int, ProjectRole> { [ReadableProject] = ProjectRole.Viewer, [WritableProject] = ProjectRole.Editor },
        includesOpen: true,
        notOpen: [9]);

    /// <summary>A key confined to project 1 (no openly reachable sources).</summary>
    private static readonly ProjectAccessScope ConfinedKey = TestProjectAccess.ReadableIn(ReadableProject);

    [Test]
    public async Task AnAdmin_IsNotChecked_AndNothingIsRead()
    {
        var resolver = new FixedResolver(ResolvedProjectResource.Missing);
        var authorizer = TestProjectAccess.Authorizer(TestProjectAccess.Unrestricted, resolver);

        await authorizer.DemandAsync(new ProjectCheck(ProjectResource.Project(OtherProject), ProjectAction.ManageMembers), CancellationToken.None);

        resolver.Calls.Should().Be(0);
    }

    [Test]
    public async Task TheFastPath_ReadsNothing()
    {
        var resolver = new FixedResolver(ResolvedProjectResource.Missing);
        var authorizer = TestProjectAccess.Authorizer(TestProjectAccess.FastPath, resolver);

        await authorizer.DemandAsync(new ProjectCheck(ProjectResource.Query(5), ProjectAction.Write), CancellationToken.None);

        resolver.Calls.Should().Be(0);
    }

    [Test]
    public async Task AResourceInAnUnreadableProject_IsNotFound_LikeAMissingOne()
    {
        var unreadable = Authorizer(Member, ResolvedProjectResource.InProject(OtherProject));
        var missing = Authorizer(Member, ResolvedProjectResource.Missing);

        var readUnreadable = () => unreadable.DemandAsync(new ProjectCheck(ProjectResource.Documentation(4), ProjectAction.Read), CancellationToken.None);
        var readMissing = () => missing.DemandAsync(new ProjectCheck(ProjectResource.Documentation(4), ProjectAction.Read), CancellationToken.None);

        (await readUnreadable.Should().ThrowAsync<BeaconNotFoundException>()).Which.Message.Should().Be("Documentation 4 not found.");
        (await readMissing.Should().ThrowAsync<BeaconNotFoundException>()).Which.Message.Should().Be("Documentation 4 not found.");
    }

    [Test]
    public async Task AResourceAnsweredAsNotFound_IsLogged_WithWhyAndWhoAsked()
    {
        var unreadableLogs = new LogRecorder();
        var missingLogs = new LogRecorder();
        var unreadable = Authorizer(Member, ResolvedProjectResource.InProject(OtherProject), logs: unreadableLogs);
        var missing = Authorizer(Member, ResolvedProjectResource.Missing, logs: missingLogs);

        await FluentActions.Awaiting(() => unreadable.DemandAsync(new ProjectCheck(ProjectResource.Query(4), ProjectAction.Write), CancellationToken.None))
            .Should().ThrowAsync<BeaconNotFoundException>();
        await FluentActions.Awaiting(() => missing.DemandAsync(new ProjectCheck(ProjectResource.Query(4), ProjectAction.Read), CancellationToken.None))
            .Should().ThrowAsync<BeaconNotFoundException>();

        var hidden = unreadableLogs.Entries.Should().ContainSingle().Subject;
        hidden.Level.Should().Be(LogLevel.Warning);
        hidden.Message.Should().Contain("Query 4").And.Contain("action Write").And.Contain("reason not-readable").And.Contain("principal User").And.Contain("user 3");
        var absent = missingLogs.Entries.Should().ContainSingle().Subject;
        absent.Level.Should().Be(LogLevel.Information);
        absent.Message.Should().Contain("reason missing");
    }

    [Test]
    public async Task AReadableResource_WhoseActionIsRefused_IsForbidden_AndLoggedWithIdsOnly()
    {
        var logs = new LogRecorder();
        var authorizer = Authorizer(Member, ResolvedProjectResource.InProject(ReadableProject), logs: logs);

        var act = () => authorizer.DemandAsync(new ProjectCheck(ProjectResource.DocumentationSection(8), ProjectAction.Write), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("DocumentationSection (Write)").And.Contain("8").And.Contain("not-granted").And.Contain("user 3").And.NotContain("ext-ada");
    }

    [Test]
    public async Task AWritableProject_AllowsTheAction()
    {
        var authorizer = Authorizer(Member, ResolvedProjectResource.InProject(WritableProject));

        await authorizer.DemandAsync(new ProjectCheck(ProjectResource.DocumentationSection(8), ProjectAction.Write), CancellationToken.None);
    }

    [Test]
    public async Task OpenlyReachableSources_AreReadable_WithoutAProjectHoldingThem()
    {
        var capture = new SqlCapture();
        var authorizer = Authorizer(Member, ResolvedProjectResource.ThroughSources([4, 5]), capture);

        await authorizer.DemandAsync(new ProjectCheck(ProjectResource.Query(7), ProjectAction.Read), CancellationToken.None);

        capture.Commands.Should().BeEmpty("openly reachable sources need no link lookup");
    }

    [TestCase(WritableProject, true)]
    [TestCase(OtherProject, false)]
    public async Task SourcesNotOpenlyReachable_NeedOneAllowedProjectHoldingThemAll(int holder, bool allowed)
    {
        var capture = new SqlCapture().ThenRows([holder]);
        var authorizer = Authorizer(Member, ResolvedProjectResource.ThroughSources([4, 9]), capture);

        var act = () => authorizer.DemandAsync(new ProjectCheck(ProjectResource.Query(7), ProjectAction.Read), CancellationToken.None);

        if (allowed)
        {
            await act.Should().NotThrowAsync();
        }
        else
        {
            await act.Should().ThrowAsync<BeaconNotFoundException>();
        }

        capture.Commands.Should().ContainSingle().Which.Should().Contain("project_data_sources").And.Contain("GROUP BY");
    }

    [Test]
    public async Task AQueryWithoutSteps_IsItsOwnersToReadAndWrite_AndNoOneElses()
    {
        // TestProjectAccess.Actors() is "ext-ada"; the member scope reaches the queries without steps "ext-ada" owns.
        var owner = TestProjectAccess.Scope(
            new Dictionary<int, ProjectRole> { [WritableProject] = ProjectRole.Editor },
            includesOpen: true,
            notOpen: [],
            queryOwnerUserId: "ext-ada");
        var own = ResolvedProjectResource.ThroughQuery([], "ext-ada");
        var someoneElses = ResolvedProjectResource.ThroughQuery([], "ext-ben");
        var ownerless = ResolvedProjectResource.ThroughQuery([], null);

        await Authorizer(owner, own).DemandAsync(new ProjectCheck(ProjectResource.Query(7), ProjectAction.Read), CancellationToken.None);
        await Authorizer(owner, own).DemandAsync(new ProjectCheck(ProjectResource.Query(7), ProjectAction.Write), CancellationToken.None);
        var run = () => Authorizer(owner, own).DemandAsync(new ProjectCheck(ProjectResource.Query(7), ProjectAction.Run), CancellationToken.None);
        var otherOwner = () => Authorizer(owner, someoneElses).DemandAsync(new ProjectCheck(ProjectResource.Query(7), ProjectAction.Read), CancellationToken.None);
        var noOwner = () => Authorizer(owner, ownerless).DemandAsync(new ProjectCheck(ProjectResource.Query(7), ProjectAction.Read), CancellationToken.None);
        var openButNotOwner = () => Authorizer(Member, own).DemandAsync(new ProjectCheck(ProjectResource.Query(7), ProjectAction.Read), CancellationToken.None);

        await run.Should().ThrowAsync<BeaconForbiddenException>("the owner reads and writes its query, nothing more");
        await otherOwner.Should().ThrowAsync<BeaconNotFoundException>();
        await noOwner.Should().ThrowAsync<BeaconNotFoundException>("a query without steps and without an owner is an Admin's");
        await openButNotOwner.Should().ThrowAsync<BeaconNotFoundException>("no data source is not the same as openly reachable");
    }

    [Test]
    public async Task ARunWithoutDataSources_IsAnAdminsAlone()
    {
        var resolver = new FixedResolver(ResolvedProjectResource.AdminOnly);
        var member = TestProjectAccess.Authorizer(Member, resolver, new SqlCapture().Factory());

        var act = () => member.DemandAsync(new ProjectCheck(ProjectResource.Run(6), ProjectAction.Read), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconNotFoundException>();
    }

    [Test]
    public async Task TheRecordedOwner_ManagesWhereItMayWrite_AndAnotherEditorDoesNot()
    {
        var restrictedEditor = TestProjectAccess.Scope(new Dictionary<int, ProjectRole> { [WritableProject] = ProjectRole.Editor }, includesOpen: false, notOpen: []);
        var owned = new ResolvedProjectResource { ProjectId = WritableProject, OwnerUserId = "ext-ada" };
        var notOwned = new ResolvedProjectResource { ProjectId = WritableProject, OwnerUserId = "ext-ben" };

        await Authorizer(restrictedEditor, owned).DemandAsync(new ProjectCheck(ProjectResource.DataContract(3), ProjectAction.Manage), CancellationToken.None);
        var act = () => Authorizer(restrictedEditor, notOwned).DemandAsync(new ProjectCheck(ProjectResource.DataContract(3), ProjectAction.Manage), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>("Manage in a restricted project needs ProjectAdmin, or being the owner");
    }

    [Test]
    public async Task ALegacyRun_IsHiddenFromACallerConfinedToAProjectList()
    {
        var legacy = new ResolvedProjectResource { SourceSets = [[4]], IsLegacyRun = true };

        await Authorizer(Member, legacy).DemandAsync(new ProjectCheck(ProjectResource.Run(6), ProjectAction.Read), CancellationToken.None);
        var act = () => Authorizer(ConfinedKey, legacy, new SqlCapture().ThenRows([ReadableProject])).DemandAsync(new ProjectCheck(ProjectResource.Run(6), ProjectAction.Read), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconNotFoundException>("even though its project holds the run's current sources");
    }

    [Test]
    public async Task ALegacyRun_IsReadByAMemberWhenMembershipIsRequired_ThroughItsQuerysCurrentSources()
    {
        // Membership required everywhere: no openly reachable sources, but the caller carries no project list.
        var memberUnderTheSwitch = TestProjectAccess.Scope(new Dictionary<int, ProjectRole> { [ReadableProject] = ProjectRole.Viewer }, includesOpen: false, notOpen: []);
        var legacy = new ResolvedProjectResource { SourceSets = [[4]], IsLegacyRun = true };

        await Authorizer(memberUnderTheSwitch, legacy, new SqlCapture().ThenRows([ReadableProject]))
            .DemandAsync(new ProjectCheck(ProjectResource.Run(6), ProjectAction.Read), CancellationToken.None);
        var elsewhere = () => Authorizer(memberUnderTheSwitch, legacy, new SqlCapture().ThenRows([OtherProject]))
            .DemandAsync(new ProjectCheck(ProjectResource.Run(6), ProjectAction.Read), CancellationToken.None);

        await elsewhere.Should().ThrowAsync<BeaconNotFoundException>();
    }

    [Test]
    public async Task SharedSourceState_NeedsWriteInEveryLinkingProject()
    {
        var everywhere = new SqlCapture().ThenRows([WritableProject]);
        var notEverywhere = new SqlCapture().ThenRows([WritableProject], [ReadableProject]);
        var source = ResolvedProjectResource.ThroughSources([4]);

        await Authorizer(Member, source, everywhere)
            .DemandAsync(ProjectCheck.SharedSource(ProjectResource.DataSource(4), ProjectAction.Write), CancellationToken.None);
        var act = () => Authorizer(Member, source, notEverywhere)
            .DemandAsync(ProjectCheck.SharedSource(ProjectResource.DataSource(4), ProjectAction.Write), CancellationToken.None);

        (await act.Should().ThrowAsync<BeaconForbiddenException>()).Which.Message.Should().Contain("every project that links it");
    }

    [Test]
    public async Task ASignalWithoutAProject_IsAnAdminsAlone_EvenOnTheFastPath()
    {
        var resolver = new FixedResolver(ResolvedProjectResource.AdminOnly);
        var nonAdmin = TestProjectAccess.Authorizer(TestProjectAccess.FastPath, resolver, new SqlCapture().Factory());
        var admin = TestProjectAccess.Authorizer(TestProjectAccess.Unrestricted, resolver, new SqlCapture().Factory());

        var act = () => nonAdmin.DemandAsync(new ProjectCheck(ProjectResource.QuerySignal(11), ProjectAction.Query), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconNotFoundException>();
        await admin.DemandAsync(new ProjectCheck(ProjectResource.QuerySignal(11), ProjectAction.Query), CancellationToken.None);
    }

    [Test]
    public async Task TheBehaviour_StopsAnUnreadableRequestBeforeItsHandler()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Authorizer(Member, ResolvedProjectResource.InProject(OtherProject)));
        await using var provider = services.BuildServiceProvider();
        var behaviour = new ProjectAuthorizationBehavior<GetProjectDetailQuery, GetProjectDetailResult>(provider);
        var reached = false;

        var act = () => behaviour.Handle(
            new GetProjectDetailQuery(OtherProject),
            _ =>
            {
                reached = true;

                return Task.FromResult(new GetProjectDetailResult(null));
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<BeaconNotFoundException>();
        reached.Should().BeFalse();
    }

    [Test]
    public async Task TheBehaviour_LetsARequestWithoutChecksThrough_WithoutResolvingTheAuthorizer()
    {
        var provider = new Mock<IServiceProvider>(MockBehavior.Strict);
        var behaviour = new ProjectAuthorizationBehavior<PlainRequest, int>(provider.Object);

        var result = await behaviour.Handle(new PlainRequest(), _ => Task.FromResult(42), CancellationToken.None);

        result.Should().Be(42);
        provider.VerifyNoOtherCalls();
    }

    [Test]
    public void TheBehaviour_IsRegisteredForEveryRequest_ByTheCoreRegistration()
    {
        using var provider = CoreServices(withRequest: true).BuildServiceProvider();

        provider.GetServices<IPipelineBehavior<GetProjectDetailQuery, GetProjectDetailResult>>()
            .Should().ContainSingle(x => x is ProjectAuthorizationBehavior<GetProjectDetailQuery, GetProjectDetailResult>);
    }

    [Test]
    public async Task OnTheSlowPath_TheEvaluatorAddsOneCommand_AndACheckReadsOnlyItsResource()
    {
        // The snapshot: project 2 is restricted and the caller an Editor member of it. Then the check's own lookup.
        var capture = new SqlCapture()
            .ThenRows([1, WritableProject, true, false, (int)ProjectRole.Editor])
            .ThenScalar(true);
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.Enabled = true;
        var actor = new BeaconActor("ext-ada", false) { Kind = BeaconPrincipalKind.User, BeaconUserId = 7, IsActiveUser = true, RoleLevel = 2 };
        var actors = new Mock<IBeaconActorAccessor>();
        actors.Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(actor);
        var provider = new Mock<IBeaconAuthorizationProvider>();
        provider.Setup(x => x.HasWritePermissionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var services = new ServiceCollection().AddSingleton(provider.Object).BuildServiceProvider();
        var evaluator = new ProjectAccessEvaluator(
            actors.Object,
            capture.Factory(),
            new DatabaseProjectMembershipSource(configuration, capture.Factory()),
            Mock.Of<IRunAsCeilingSource>(),
            services,
            configuration,
            Microsoft.Extensions.Options.Options.Create(new Beacon.Core.Configuration.ProjectAuthorizationOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectAccessEvaluator>.Instance,
            new OwnerRunsAsSystemLog(TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<OwnerRunsAsSystemLog>.Instance));
        var authorizer = new ProjectRequestAuthorizer(
            evaluator,
            new ProjectResourceResolver(),
            capture.Factory(),
            actors.Object,
            TestPermissions.None,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectRequestAuthorizer>.Instance);

        await authorizer.DemandAsync(new ProjectCheck(ProjectResource.Project(WritableProject), ProjectAction.Write), CancellationToken.None);
        var scope = await evaluator.GetScopeAsync(CancellationToken.None);

        scope.IsUnrestricted.Should().BeFalse();
        scope.For(ProjectAction.Write).Ids.Should().Equal(WritableProject);
        capture.Commands.Should().HaveCount(2, "one snapshot for the request, one lookup for the checked resource");
        capture.Commands[0].Should().Contain("UNION ALL");
        capture.Commands[1].Should().Contain("FROM beacon.projects");
    }

    [Test]
    public async Task AJobInAScopeMarkedAsSystemWork_IsNotChecked_AndStillRuns()
    {
        // A scheduled subscription run has no HttpContext; its job host marks the job's scope as system work, so it runs
        // as the system, unrestricted, and the project machinery reads nothing.
        var capture = new SqlCapture().AnsweringCountsWithOne();
        var services = CoreServices(withRequest: false);
        services.AddSingleton(capture.Factory());
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISystemWorkScope>().MarkAsSystemWork();

        var access = await scope.ServiceProvider.GetRequiredService<IProjectAccessEvaluator>().GetScopeAsync(CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<ProjectRequestAuthorizer>()
            .DemandAsync(new ProjectCheck(ProjectResource.Subscription(42), ProjectAction.Run), CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<IJobService>().ExecuteQuery(42, CancellationToken.None);

        access.IsUnrestricted.Should().BeTrue();
        capture.Commands.Should().ContainSingle("the job reads its subscription and, with none there, stops; nothing else ran")
            .Which.Should().Contain("FROM beacon.subscriptions");
        capture.Commands.Should().NotContain(x => x.Contains("UNION ALL"), "no project access snapshot is read for a job");
    }

    [Test]
    public async Task WorkWithoutARequest_InAnUnmarkedScope_GetsNoProjectAccess_AndIsRefused()
    {
        // No request and nobody marked the scope as system work (a host's own job runner, a stray background task):
        // there is no caller to authorize, so it reaches nothing.
        var capture = new SqlCapture().AnsweringCountsWithOne();
        var services = CoreServices(withRequest: false);
        services.AddSingleton(capture.Factory());
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var access = await scope.ServiceProvider.GetRequiredService<IProjectAccessEvaluator>().GetScopeAsync(CancellationToken.None);
        var act = () => scope.ServiceProvider.GetRequiredService<ProjectRequestAuthorizer>()
            .DemandAsync(new ProjectCheck(ProjectResource.Subscription(42), ProjectAction.Run), CancellationToken.None);

        access.Should().BeSameAs(ProjectAccessScope.None);
        await act.Should().ThrowAsync<BeaconNotFoundException>();
        capture.Commands.Should().NotContain(x => x.Contains("UNION ALL"), "nobody has nothing to read a snapshot for");
    }

    private static ProjectRequestAuthorizer Authorizer(
        ProjectAccessScope scope,
        ResolvedProjectResource resolved,
        SqlCapture? capture = null,
        LogRecorder? logs = null)
    {
        return TestProjectAccess.Authorizer(
            scope,
            new FixedResolver(resolved),
            (capture ?? new SqlCapture()).Factory(),
            logger: logs?.For<ProjectRequestAuthorizer>());
    }

    private static ServiceCollection CoreServices(bool withRequest)
    {
        var services = new ServiceCollection();
        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Beacon:EncryptionKey"] = Convert.ToBase64String(new byte[32]) })
            .Build();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>());
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = withRequest ? new DefaultHttpContext() : null });
        services.AddScoped<IBeaconUserContext, HttpContextUserContext>();

        return services;
    }

    public sealed record PlainRequest : IRequest<int>;

    // Resolves every resource to the same place, and counts the lookups.
    private sealed class FixedResolver(ResolvedProjectResource resolved) : IProjectResourceResolver
    {
        public int Calls { get; private set; }

        public Task<ResolvedProjectResource> ResolveAsync(BeaconContext context, ProjectResource resource, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(resolved);
        }
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}

using System.Security.Claims;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Enums;
using Beacon.Core.Exceptions;
using Beacon.Core.HostData;
using Beacon.Core.Models;
using Beacon.Core.Models.Ai;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.Core.Worker;
using Beacon.Core.Worker.Services;
using Beacon.SampleProject.Warp;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Warp.Core.Handlers;
using ActorEntity = Beacon.Core.Data.Entities.AiActor;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// Scheduled work runs as its owner and is re-authorized at every run, right before any SQL or LLM call: a subscription
/// as its owner (Run on one project holding every step's source), a data contract as its owner (Run on its source), an
/// AI actor's background cycle as its creator (Query on its source). A refused run is skipped — recorded, nothing ran,
/// the job does not fail. The real job code, query service, evaluator and actor accessor run over in-memory rows (no
/// database, §4.7); a data source connection, a provider and an LLM call stand for "the work ran". Project 1 is open
/// (data source 10), project 2 restricted (data source 20); user 5 is an Editor.
/// </summary>
[TestFixture]
public class ScheduledWorkRunAsTests
{
    private const int OpenProject = 1;
    private const int RestrictedProject = 2;
    private const int OpenSource = 10;
    private const int RestrictedSource = 20;
    private const int OwnerId = 5;
    private const string Owner = "ext-owner";
    private const int SubscriptionId = 100;
    private const int ContractId = 200;
    private const int ActorId = 300;

    private ProjectAccessWorld _world = null!;
    private LogRecorder _logs = null!;
    private List<Subscription> _subscriptions = null!;
    private List<DataContract> _contracts = [];
    private Mock<IDataSourceConnectionResolver> _connections = null!;
    private int _connectionsOpened;

    [SetUp]
    public void SetUp()
    {
        _world = new ProjectAccessWorld();
        _world.Project(OpenProject);
        _world.Project(RestrictedProject, requiresMembership: true);
        _world.Source(OpenSource, OpenProject);
        _world.Source(RestrictedSource, RestrictedProject);
        _world.User(OwnerId, Owner);
        _logs = new LogRecorder();
        _subscriptions = [];
        _world.Serve(_subscriptions);
        _connectionsOpened = 0;
        _connections = new Mock<IDataSourceConnectionResolver>();
        _connections
            .Setup(x => x.GetConnectionString(It.IsAny<DataSource>()))
            .Callback(() => _connectionsOpened++)
            .Throws(new ExecutionReachedException());
    }

    [Test]
    public async Task ADisabledOwner_GetsTheRunSkipped_WithoutASingleSqlCommand()
    {
        Subscribe(Owner, OpenSource);
        _world.Users.First(x => x.Id == OwnerId).IsEnabled = false;

        await RunSubscriptionAsync();

        _connectionsOpened.Should().Be(0, "nothing connects to the data source before the owner is re-authorized");
        ShouldBeSkipped([OpenSource]);
    }

    [Test]
    public async Task AnOwnerWhoLostTheirMembership_GetsTheRunSkipped()
    {
        Subscribe(Owner, RestrictedSource);

        await RunSubscriptionAsync();

        _connectionsOpened.Should().Be(0);
        ShouldBeSkipped([RestrictedSource]);
        _logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning && x.Message.StartsWith($"Scheduled work skipped: subscription {SubscriptionId}, action Run, owner {OwnerId}"));
    }

    [Test]
    public async Task AnOwnerWhoMayRunTheQuery_RunsIt()
    {
        Subscribe(Owner, RestrictedSource);
        _world.Member(RestrictedProject, OwnerId, ProjectRole.Viewer);

        var act = () => RunSubscriptionAsync();

        // The run reached the data source's connection (which the test double refuses), so it was not skipped.
        await act.Should().ThrowAsync<QueryExecutionFailedException>("a Viewer member may run saved SQL in the project");
        _connectionsOpened.Should().Be(1);
        History().Should().BeEmpty();
    }

    [Test]
    public async Task AnOwnerlessSubscription_RunsOnOpenlyReachableData()
    {
        Subscribe(null, OpenSource);

        var act = () => RunSubscriptionAsync();

        await act.Should().ThrowAsync<QueryExecutionFailedException>();
        _connectionsOpened.Should().Be(1, "the run reached the data source");
        History().Should().BeEmpty();
    }

    [Test]
    public async Task AnOwnerlessSubscription_IsSkipped_OnDataOnlyARestrictedProjectHolds()
    {
        Subscribe(null, RestrictedSource);

        await RunSubscriptionAsync();

        _connectionsOpened.Should().Be(0);
        ShouldBeSkipped([RestrictedSource]);
    }

    [Test]
    public async Task AnOwnerlessSubscription_IsAlwaysSkipped_WhenEveryProjectRequiresMembership()
    {
        _world.RequireProjectMembership = true;
        Subscribe(null, OpenSource);

        await RunSubscriptionAsync();

        _connectionsOpened.Should().Be(0);
        ShouldBeSkipped([OpenSource]);
    }

    [Test]
    public async Task AnOwnerWithoutAUser_OnAHostWithoutUserManagement_RunsAsTheSystem()
    {
        _world.UserManagement = false;
        _world.Users.Clear();
        Subscribe("host-principal", RestrictedSource);

        var act = () => RunSubscriptionAsync();

        await act.Should().ThrowAsync<QueryExecutionFailedException>();
        _connectionsOpened.Should().Be(1, "the run reached the data source");
        _logs.Entries.Should().ContainSingle(x => x.Message.Contains("Scheduled work of owner host-principal runs as the system"))
            .Which.Level.Should().Be(LogLevel.Warning, "the owner's access is not re-checked on such a host");
    }

    [Test]
    public async Task ARunARequestAsksFor_RunsAsTheRequestsCaller_AndARefusalIsA403()
    {
        // The request's caller, an Editor without membership, may not run the restricted project's query: a test delivery
        // is refused (403), not recorded as a skipped run.
        Subscribe(null, RestrictedSource);
        var request = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Owner)], "Cookies"))
        };

        var act = () => RunSubscriptionAsync(request);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        _connectionsOpened.Should().Be(0);
        History().Should().BeEmpty();
    }

    [Test]
    public async Task AContractOfADisabledOwner_IsNotEvaluated_AndTheJobDoesNotFail()
    {
        _world.Users.First(x => x.Id == OwnerId).IsEnabled = false;
        var providers = new Mock<IDataSourceProviderFactory>(MockBehavior.Strict);

        var contract = await EvaluateContractAsync(providers.Object);

        providers.VerifyNoOtherCalls();
        _world.Saved.OfType<DataQualityEvaluation>().Should().BeEmpty();
        _logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning && x.Message.StartsWith($"Scheduled work skipped: data contract {ContractId}, action Run"));
        contract.OwnerNotAuthorizedSince.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1), "the skip is recorded on the contract for its readers");
    }

    [Test]
    public async Task ASkippedContractEvaluation_KeepsTheFirstSkipOfTheSeries()
    {
        _world.Users.First(x => x.Id == OwnerId).IsEnabled = false;
        var firstSkip = new DateTime(2026, 10, 1, 6, 0, 0, DateTimeKind.Utc);

        var contract = await EvaluateContractAsync(Mock.Of<IDataSourceProviderFactory>(MockBehavior.Strict), firstSkip);

        contract.OwnerNotAuthorizedSince.Should().Be(firstSkip);
    }

    [Test]
    public async Task AContractEvaluationARequestAsksFor_IsRefused_AndRecordsNoSkip()
    {
        _world.Users.First(x => x.Id == OwnerId).IsEnabled = false;
        var request = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Owner)], "Cookies"))
        };

        var act = () => EvaluateContractAsync(Mock.Of<IDataSourceProviderFactory>(MockBehavior.Strict), request: request);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        _contracts.Single().OwnerNotAuthorizedSince.Should().BeNull("only a scheduled evaluation run as the owner is a skip");
    }

    [Test]
    public async Task AContractOfAnOwnerWhoMayRunIt_IsEvaluated()
    {
        var providers = new Mock<IDataSourceProviderFactory>();
        providers
            .Setup(x => x.GetProvider(It.IsAny<DataSourceType>()))
            .Throws(new ExecutionReachedException());

        var act = () => EvaluateContractAsync(providers.Object);

        await act.Should().ThrowAsync<ExecutionReachedException>();
    }

    [Test]
    public async Task AnAiActorsCycle_IsSkippedAndRecorded_WhenItsCreatorMayNotQueryItsSource()
    {
        // A Viewer member reads and runs saved SQL in the project, but an AI actor's cycle works ad hoc (Query).
        _world.Member(RestrictedProject, OwnerId, ProjectRole.Viewer);
        var llm = new Mock<ILlmProvider>(MockBehavior.Strict);
        var actor = Actor(RestrictedSource);

        await RunActorCycleAsync(actor, llm.Object);

        llm.VerifyNoOtherCalls();
        var execution = _world.Saved.OfType<AiActorExecution>().Should().ContainSingle().Subject;
        execution.Phase.Should().Be(AiActorExecutionPhase.Skipped);
        execution.ErrorMessage.Should().Be(AiActorService.OwnerNotAuthorizedMessage);
        actor.Status.Should().Be(AiActorStatus.Active, "a skipped cycle is not a failed actor");
        actor.ThinkCount.Should().Be(0);
    }

    [Test]
    public async Task AnAiActorsCycle_ReachesTheLlm_WhenItsCreatorMayQueryItsSource()
    {
        _world.Member(RestrictedProject, OwnerId, ProjectRole.Editor);
        var llm = new Mock<ILlmProvider>();
        llm
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ExecutionReachedException());

        await RunActorCycleAsync(Actor(RestrictedSource), llm.Object);

        llm.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _world.Saved.OfType<AiActorExecution>().Should().NotContain(x => x.Phase == AiActorExecutionPhase.Skipped);
    }

    [Test]
    public void TheOwnerARunActsAs_IsSetOnce()
    {
        var runAs = new RunAsScope(new HttpContextAccessor());
        runAs.RunAs(Owner);

        var act = () => runAs.RunAs("ext-other");

        act.Should().Throw<InvalidOperationException>().WithMessage("*set once*");
        runAs.OwnerUserId.Should().Be(Owner);
    }

    [Test]
    public void TheOwnerARunActsAs_IsNeverSetInARequest()
    {
        var runAs = new RunAsScope(new HttpContextAccessor { HttpContext = new DefaultHttpContext() });

        var act = () => runAs.RunAs(Owner);

        act.Should().Throw<InvalidOperationException>().WithMessage("*request*");
        runAs.IsSet.Should().BeFalse();
    }

    [Test]
    public async Task TheOwnerARunActsAs_IsNeverSetAfterTheScopesCallerWasResolved()
    {
        await using var provider = JobServices();
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISystemWorkScope>().MarkAsSystemWork();
        var system = await scope.ServiceProvider.GetRequiredService<IProjectAccessEvaluator>().GetScopeAsync(CancellationToken.None);

        var act = () => scope.ServiceProvider.GetRequiredService<IRunAsScope>().RunAs(Owner);

        system.IsUnrestricted.Should().BeTrue();
        act.Should().Throw<InvalidOperationException>().WithMessage("*already resolved*");
    }

    [Test]
    public async Task AScopeRunAsAnOwner_IsThatOwner_EvenWhenTheJobHostMarkedItAsSystemWork()
    {
        await using var provider = JobServices();
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISystemWorkScope>().MarkAsSystemWork();
        scope.ServiceProvider.GetRequiredService<IRunAsScope>().RunAs(Owner);

        var actor = await scope.ServiceProvider.GetRequiredService<IBeaconActorAccessor>().GetCurrentAsync(CancellationToken.None);
        var access = await scope.ServiceProvider.GetRequiredService<IProjectAccessEvaluator>().GetScopeAsync(CancellationToken.None);

        actor.UserId.Should().Be(Owner);
        actor.Kind.Should().Be(BeaconPrincipalKind.Owner);
        access.IsUnrestricted.Should().BeFalse();
        access.Allows(OpenProject, ProjectAction.Run).Should().BeTrue();
        access.Allows(RestrictedProject, ProjectAction.Read).Should().BeFalse();
    }

    [Test]
    public async Task MaintenanceInAScopeMarkedAsSystemWork_StillRunsAsTheSystem_AndAnUnmarkedScopeGetsNothing()
    {
        await using var provider = JobServices();
        await using var marked = provider.CreateAsyncScope();
        await using var unmarked = provider.CreateAsyncScope();
        marked.ServiceProvider.GetRequiredService<ISystemWorkScope>().MarkAsSystemWork();

        var system = await marked.ServiceProvider.GetRequiredService<IProjectAccessEvaluator>().GetScopeAsync(CancellationToken.None);
        var nobody = await unmarked.ServiceProvider.GetRequiredService<IProjectAccessEvaluator>().GetScopeAsync(CancellationToken.None);

        system.IsUnrestricted.Should().BeTrue();
        nobody.IsUnrestricted.Should().BeFalse();
        nobody.For(ProjectAction.Read).Ids.Should().BeEmpty();
        nobody.For(ProjectAction.Read).IncludesOpen.Should().BeFalse();
    }

    [Test]
    public async Task TheSampleHostsSystemWorkBehavior_MarksTheJobsScope_BeforeTheJobRuns()
    {
        // The behaviour the sample's Warp pipeline runs around every job, executed with a real scope: inside the job the
        // scope is system work, so project access is unrestricted; before it, an unmarked scope without a request gets
        // nothing.
        await using var provider = JobServices();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var systemWork = services.GetRequiredService<ISystemWorkScope>();
        var behavior = new SystemWorkBehavior<SampleJobRequest, int>(systemWork);
        var markedBefore = systemWork.IsSystemWork;
        var markedInside = false;
        ProjectAccessScope? accessInside = null;

        var answer = await behavior.HandleAsync(
            new SampleJobRequest(),
            async (_, cancellationToken) =>
            {
                markedInside = systemWork.IsSystemWork;
                accessInside = await services.GetRequiredService<IProjectAccessEvaluator>().GetScopeAsync(cancellationToken);

                return 42;
            },
            CancellationToken.None);

        markedBefore.Should().BeFalse();
        markedInside.Should().BeTrue();
        accessInside!.IsUnrestricted.Should().BeTrue("a job's own work runs as the system");
        answer.Should().Be(42, "the job's answer passes through");
    }

    [Test]
    public void TheSampleHostsWarpPipeline_MarksEveryJobsScopeAsSystemWork()
    {
        // The Warp source generator registers the sample host's pipeline behaviours; the job's own run-as decides the
        // rest. Without the marker every job without a request would get no project access.
        var registration = typeof(SystemWorkBehavior<,>).Assembly
            .GetType("Warp.Core.Handlers.Generated.WarpMediatorServiceExtensions", throwOnError: true)!
            .GetMethod("AddWarpMediator", [typeof(IServiceCollection)])!;
        var services = new ServiceCollection();

        registration.Invoke(null, [services]);

        services.Should().Contain(x => x.ServiceType == typeof(IPipelineBehavior<,>) && x.ImplementationType == typeof(SystemWorkBehavior<,>));
    }

    private void Subscribe(string? owner, int dataSourceId)
    {
        var source = _world.DataSources.First(x => x.Id == dataSourceId);
        var query = new Query
        {
            Id = 1000,
            Name = "Late payments",
            Steps =
            [
                new QueryStep { QueryId = 1000, StepOrder = 1, DataSourceId = dataSourceId, DataSource = source, SqlValue = "SELECT 1" }
            ]
        };
        _subscriptions.Add(new Subscription
        {
            Id = SubscriptionId,
            QueryId = query.Id,
            Query = query,
            CronExpression = "0 * * * *",
            OwnerUserId = owner,
            NotificationTrigger = NotificationTrigger.Always
        });
    }

    private void ShouldBeSkipped(int[] dataSourceIds)
    {
        var run = History().Should().ContainSingle().Subject;
        run.SubscriptionId.Should().Be(SubscriptionId);
        run.NotificationStatus.Should().Be(NotificationStatus.Skipped);
        run.Comment.Should().Be(NotificationFailureReasons.OwnerNotAuthorized);
        run.ResultCount.Should().Be(0);
        run.DataSourceIds.Should().Equal(dataSourceIds);
        NotificationFailureReasons.Displayable(run.Comment).Should().Be(NotificationFailureReasons.OwnerNotAuthorized);
    }

    private List<QueryExecutionHistory> History()
    {
        return _world.Saved
            .OfType<QueryExecutionHistory>()
            .ToList();
    }

    // One job: the job host's scope, marked as system work as the sample's Warp pipeline marks it.
    private async Task RunSubscriptionAsync(HttpContext? request = null)
    {
        await using var provider = JobServices(request);
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISystemWorkScope>().MarkAsSystemWork();

        await scope.ServiceProvider.GetRequiredService<JobService>().ExecuteQuery(SubscriptionId, CancellationToken.None);
    }

    private async Task<DataContract> EvaluateContractAsync(
        IDataSourceProviderFactory providers,
        DateTime? ownerNotAuthorizedSince = null,
        HttpContext? request = null)
    {
        var source = _world.DataSources.First(x => x.Id == RestrictedSource);
        _world.Member(RestrictedProject, OwnerId, ProjectRole.Viewer);
        var contract = new DataContract
        {
            Id = ContractId,
            DataSourceId = source.Id,
            DataSource = source,
            SchemaName = "sales",
            TableName = "orders",
            Name = "Orders are complete",
            CronExpression = "0 * * * *",
            OwnerUserId = Owner,
            OwnerNotAuthorizedSince = ownerNotAuthorizedSince,
            Rules = [new DataContractRule { Id = 1, DataContractId = ContractId, Name = "volume", RuleType = DataContractRuleType.Volume, Configuration = "{}" }]
        };
        _contracts = [contract];
        _world.Serve(_contracts);

        await using var provider = JobServices(request, providers);
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISystemWorkScope>().MarkAsSystemWork();

        await scope.ServiceProvider.GetRequiredService<JobService>().EvaluateDataContract(ContractId, CancellationToken.None);

        return contract;
    }

    private ActorEntity Actor(int dataSourceId)
    {
        var actor = new ActorEntity
        {
            Id = ActorId,
            Name = "Payments watcher",
            Instructions = "Watch late payments.",
            DataSourceId = dataSourceId,
            DataSource = _world.DataSources.First(x => x.Id == dataSourceId),
            Status = AiActorStatus.Active,
            CreatedByUserId = Owner
        };
        _world.Serve(new List<ActorEntity> { actor });
        _world.Serve(new List<Query>());

        return actor;
    }

    private async Task RunActorCycleAsync(ActorEntity actor, ILlmProvider llm)
    {
        await using var provider = JobServices();
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISystemWorkScope>().MarkAsSystemWork();
        var sp = scope.ServiceProvider;
        var service = new AiActorService(
            sp.GetRequiredService<IDbContextFactory<BeaconContext>>(),
            llm,
            Mock.Of<IDatabaseMetadataService>(),
            Mock.Of<IQueryService>(MockBehavior.Strict),
            Mock.Of<ISubscriptionService>(MockBehavior.Strict),
            Mock.Of<IBeaconScheduler>(MockBehavior.Strict),
            sp.GetRequiredService<IRunAsScope>(),
            sp.GetRequiredService<IExecutionAccessCheck>(),
            Mock.Of<IStepSqlSaveCheck>(),
            NullLogger<AiActorService>.Instance);

        await service.ExecuteThinkCycleBackgroundAsync(actor.Id);
    }

    private ServiceProvider JobServices(HttpContext? request = null, IDataSourceProviderFactory? providers = null)
    {
        var services = new ServiceCollection();
        _world.Register(services);
        services.AddSingleton<ILoggerProvider>(_logs);
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = request });
        services.AddScoped<IExecutionAccessCheck, ExecutionAccessCheck>();
        services.AddScoped<IQueryService>(x => TestQueryServices.QueryService(
            x.GetRequiredService<IDbContextFactory<BeaconContext>>(),
            _connections.Object,
            new HostDataSourceGuard(new TestHostDataSources()),
            new ConfiguredDatabaseResolver(_connections.Object, NullLogger<ConfiguredDatabaseResolver>.Instance),
            configuration: x.GetRequiredService<BeaconConfiguration>(),
            actors: x.GetRequiredService<IBeaconActorAccessor>(),
            executionAccess: x.GetRequiredService<IExecutionAccessCheck>()));
        services.AddScoped<IDataQualityEvaluationService>(x => new DataQualityEvaluationService(
            x.GetRequiredService<IDbContextFactory<BeaconContext>>(),
            new DataQualitySqlGenerator(),
            providers ?? Mock.Of<IDataSourceProviderFactory>(MockBehavior.Strict),
            TestSqlGate.Create(),
            TestConfiguredDatabases.None,
            x.GetRequiredService<IExecutionAccessCheck>(),
            TestQueryServices.DefaultSettings(),
            new QueryGuardrailService(),
            NullLogger<DataQualityEvaluationService>.Instance));
        services.AddScoped(x => new JobService(
            x.GetRequiredService<IDbContextFactory<BeaconContext>>(),
            x.GetRequiredService<IQueryService>(),
            Mock.Of<INotificationService>(MockBehavior.Strict),
            Mock.Of<ITaskService>(MockBehavior.Strict),
            Mock.Of<IAnomalyDetectionService>(MockBehavior.Strict),
            x.GetRequiredService<IDataQualityEvaluationService>(),
            x.GetRequiredService<ILogger<JobService>>(),
            x.GetRequiredService<IRunAsScope>(),
            x.GetRequiredService<IHttpContextAccessor>()));

        return services.BuildServiceProvider();
    }

    private sealed class ExecutionReachedException : Exception;

    private sealed class SampleJobRequest : IRequest<int>;
}

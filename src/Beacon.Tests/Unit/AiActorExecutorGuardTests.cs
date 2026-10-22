using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.Ai.AiActor.Models;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Services;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The ownership / lock / cap guards must hold where the executors actually run (an actor with
/// RequiresApproval = false), not only in the pure <see cref="AiActorActionGuard"/> decisions.
/// </summary>
[TestFixture]
public class AiActorExecutorGuardTests
{
    private const int ActorId = 5;
    private const int OtherActorId = 6;
    private const int DataSourceId = 9;

    private Mock<IQueryService> _queryService = null!;
    private Mock<ISubscriptionService> _subscriptionService = null!;
    private Mock<DbSet<Query>> _queriesSet = null!;
    private Mock<DbSet<Subscription>> _subscriptionsSet = null!;

    [SetUp]
    public void SetUp()
    {
        _queryService = new Mock<IQueryService>(MockBehavior.Strict);
        _subscriptionService = new Mock<ISubscriptionService>(MockBehavior.Strict);
    }

    [Test]
    public async Task ArchiveQuery_OwnedByAnotherActor_IsDenied()
    {
        var service = BuildService(queries: [new Query { Id = 1, AiActorId = OtherActorId }]);

        var action = await service.ExecuteOrProposeAsync(Actor(), Plan("ARCHIVE_QUERY", ("queryId", 1)), [], CancellationToken.None);

        AssertDenied(action, "Not owned by this actor");
    }

    [Test]
    public async Task NullPlan_IsRecordedAsAFailedAction_NotThrown()
    {
        var service = BuildService();

        var action = await service.ExecuteOrProposeAsync(Actor(), null!, [], CancellationToken.None);

        action.Success.Should().BeFalse();
        action.Proposed.Should().BeFalse();
        action.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task ArchiveQuery_OwnedButLocked_IsDenied()
    {
        var service = BuildService(queries: [new Query { Id = 1, AiActorId = ActorId, IsLocked = true }]);

        var action = await service.ExecuteOrProposeAsync(Actor(), Plan("ARCHIVE_QUERY", ("queryId", 1)), [], CancellationToken.None);

        AssertDenied(action, "Locked");
    }

    [Test]
    public async Task ArchiveQuery_OwnedAndUnlocked_Archives()
    {
        _queryService
            .Setup(x => x.DeleteQuery(1, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var service = BuildService(queries: [new Query { Id = 1, AiActorId = ActorId }]);

        var action = await service.ExecuteOrProposeAsync(Actor(), Plan("ARCHIVE_QUERY", ("queryId", 1)), [], CancellationToken.None);

        action.Success.Should().BeTrue();
        action.ResultEntityId.Should().Be(1);
        _queryService.Verify(x => x.DeleteQuery(1, It.IsAny<CancellationToken>()), Times.Once);
        _queryService.VerifyNoOtherCalls();
    }

    [Test]
    public async Task ArchiveSubscription_NotOwned_IsDenied()
    {
        var service = BuildService(subscriptions: [new Subscription { Id = 2, CronExpression = "* * * * *", QueryId = 1, AiActorId = OtherActorId }]);

        var action = await service.ExecuteOrProposeAsync(Actor(), Plan("ARCHIVE_SUBSCRIPTION", ("subscriptionId", 2)), [], CancellationToken.None);

        AssertDenied(action, "Not owned by this actor");
    }

    [Test]
    public async Task CreateSubscription_OnQueryOwnedByAnotherActor_CreatesNothing()
    {
        var service = BuildService(queries: [new Query { Id = 1, AiActorId = OtherActorId }]);

        var action = await service.ExecuteOrProposeAsync(Actor(), Plan("CREATE_SUBSCRIPTION", ("queryId", 1)), [], CancellationToken.None);

        AssertDenied(action, "Not owned by this actor");
        _subscriptionsSet.Verify(x => x.Add(It.IsAny<Subscription>()), Times.Never);
    }

    [Test]
    public async Task CreateSubscription_OverCap_CreatesNothing()
    {
        var service = BuildService(
            queries: [new Query { Id = 1, AiActorId = ActorId }],
            subscriptions: [new Subscription { Id = 2, CronExpression = "* * * * *", QueryId = 1, AiActorId = ActorId }]);

        var action = await service.ExecuteOrProposeAsync(
            Actor(maxSubscriptionsPerQuery: 1),
            Plan("CREATE_SUBSCRIPTION", ("queryId", 1)),
            [],
            CancellationToken.None);

        AssertDenied(action, "subscription limit");
        _subscriptionsSet.Verify(x => x.Add(It.IsAny<Subscription>()), Times.Never);
    }

    [Test]
    public async Task CreateSubscription_OnAnOwnedQuery_IsOwnedByTheActorsCreator()
    {
        var service = BuildService(queries: [new Query { Id = 1, AiActorId = ActorId }]);
        var actor = Actor();
        actor.CreatedByUserId = "ext-creator";

        var action = await service.ExecuteOrProposeAsync(actor, Plan("CREATE_SUBSCRIPTION", ("queryId", 1)), [], CancellationToken.None);

        action.Success.Should().BeTrue();
        _subscriptionsSet.Verify(
            x => x.Add(It.Is<Subscription>(y => y.OwnerUserId == "ext-creator" && y.AiActorId == ActorId)),
            Times.Once);
    }

    // An actor's schedule is held to the limits a person's is: a cron that fires more often than the minimum, or that is not
    // a five-field cron, creates nothing.
    [TestCase("* * * * *", "*every 5 minutes*")]
    [TestCase("*/30 * * * * *", "*five fields*")]
    [TestCase("not a cron", "*five fields*")]
    public async Task CreateSubscription_WithAScheduleOutsideTheLimits_CreatesNothing(string cron, string message)
    {
        var service = BuildService(queries: [new Query { Id = 1, AiActorId = ActorId }]);

        var action = await service.ExecuteOrProposeAsync(Actor(), Plan("CREATE_SUBSCRIPTION", ("queryId", 1), ("cronExpression", cron)), [], CancellationToken.None);

        action.Success.Should().BeFalse();
        action.ErrorMessage.Should().Match(message);
        _subscriptionsSet.Verify(x => x.Add(It.IsAny<Subscription>()), Times.Never);
    }

    [Test]
    public async Task CreateSubscription_WithAScheduleAtTheMinimum_IsCreated()
    {
        var service = BuildService(queries: [new Query { Id = 1, AiActorId = ActorId }]);

        var action = await service.ExecuteOrProposeAsync(Actor(), Plan("CREATE_SUBSCRIPTION", ("queryId", 1), ("cronExpression", "*/5 * * * *")), [], CancellationToken.None);

        action.Success.Should().BeTrue();
        _subscriptionsSet.Verify(x => x.Add(It.Is<Subscription>(y => y.CronExpression == "*/5 * * * *")), Times.Once);
    }

    [Test]
    public async Task CreateQuery_OverCap_CreatesNothing()
    {
        var service = BuildService(queries: [new Query { Id = 1, AiActorId = ActorId }]);

        var action = await service.ExecuteOrProposeAsync(
            Actor(maxQueries: 1),
            Plan("CREATE_QUERY", ("name", "n"), ("sql", "SELECT 1")),
            [],
            CancellationToken.None);

        AssertDenied(action, "query limit");
        _queriesSet.Verify(x => x.Add(It.IsAny<Query>()), Times.Never);
    }

    [Test]
    public async Task CreateQuery_SqlTheSaveCheckRefuses_IsNeverStored()
    {
        var saveCheck = RefusingSaveCheck("Only SELECT queries are permitted.");
        var service = BuildService(saveCheck: saveCheck.Object);

        var action = await service.ExecuteOrProposeAsync(Actor(), Plan("CREATE_QUERY", ("name", "n"), ("sql", "DELETE FROM accounts")), [], CancellationToken.None);

        AssertDenied(action, "Query validation failed: Only SELECT queries are permitted.");
        _queriesSet.Verify(x => x.Add(It.IsAny<Query>()), Times.Never);
        saveCheck.Verify(x => x.RejectionAsync(DataSourceId, "DELETE FROM accounts", It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task RefineQuery_SqlTheSaveCheckRefuses_LeavesTheStoredStepUntouched()
    {
        var step = new QueryStep { Id = 3, QueryId = 1, StepOrder = 1, DataSourceId = DataSourceId, SqlValue = "SELECT id FROM accounts" };
        var query = new Query { Id = 1, AiActorId = ActorId, Steps = [step] };
        var saveCheck = RefusingSaveCheck("The table ghosts does not exist.");
        var service = BuildService(queries: [query], saveCheck: saveCheck.Object);

        var action = await service.ExecuteOrProposeAsync(Actor(), Plan("REFINE_QUERY", ("queryId", 1), ("newSql", "SELECT id FROM ghosts")), [], CancellationToken.None);

        AssertDenied(action, "Refined query validation failed: The table ghosts does not exist.");
        step.SqlValue.Should().Be("SELECT id FROM accounts");
    }

    private static Mock<IStepSqlSaveCheck> RefusingSaveCheck(string reason)
    {
        var saveCheck = new Mock<IStepSqlSaveCheck>();
        saveCheck
            .Setup(x => x.RejectionAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(reason);

        return saveCheck;
    }

    private void AssertDenied(AiActorAction action, string reasonFragment)
    {
        action.Success.Should().BeFalse();
        action.Proposed.Should().BeFalse();
        action.ErrorMessage.Should().Contain(reasonFragment);
        _queryService.VerifyNoOtherCalls();
        _subscriptionService.VerifyNoOtherCalls();
    }

    private static Beacon.Core.Data.Entities.AiActor Actor(int maxQueries = 10, int maxSubscriptionsPerQuery = 10) =>
        new()
        {
            Id = ActorId,
            DataSourceId = DataSourceId,
            RequiresApproval = false,
            MaxQueries = maxQueries,
            MaxSubscriptionsPerQuery = maxSubscriptionsPerQuery
        };

    private static AiActorActionPlan Plan(string type, params (string Key, object Value)[] parameters)
    {
        var plan = new AiActorActionPlan { ActionType = type, Reasoning = "r" };
        foreach (var (key, value) in parameters)
        {
            plan.Parameters[key] = value;
        }

        return plan;
    }

    private AiActorService BuildService(IReadOnlyList<Query>? queries = null, IReadOnlyList<Subscription>? subscriptions = null, IStepSqlSaveCheck? saveCheck = null)
    {
        _queriesSet = BuildSet(queries ?? []);
        _subscriptionsSet = BuildSet(subscriptions ?? []);

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ExecutorContext(_queriesSet.Object, _subscriptionsSet.Object));

        return new AiActorService(
            factory.Object,
            Mock.Of<ILlmProvider>(MockBehavior.Strict),
            Mock.Of<IDatabaseMetadataService>(MockBehavior.Strict),
            _queryService.Object,
            _subscriptionService.Object,
            Mock.Of<IBeaconScheduler>(MockBehavior.Strict),
            Mock.Of<IRunAsScope>(),
            TestProjectAccess.ExecutionAllowed(),
            saveCheck ?? Mock.Of<IStepSqlSaveCheck>(),
            Mock.Of<IQueryProposalCheck>(MockBehavior.Strict),
            Mock.Of<IQueryVersionService>(),
            new Beacon.Core.BeaconConfiguration(),
            new RecordingSecurityAudit(),
            new SubscriptionScheduleCheck(Microsoft.Extensions.Options.Options.Create(new Beacon.Core.Configuration.SubscriptionOptions()), new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero))),
            Mock.Of<ILogger<AiActorService>>(),
            null,
            null,
            null);
    }

    private sealed class ExecutorContext(DbSet<Query> queries, DbSet<Subscription> subscriptions) : BeaconContext(Options, "beacon")
    {
        private static readonly DbContextOptions<ExecutorContext> Options =
            new DbContextOptionsBuilder<ExecutorContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(Query))
            {
                return (DbSet<TEntity>)(object)queries;
            }

            if (typeof(TEntity) == typeof(Subscription))
            {
                return (DbSet<TEntity>)(object)subscriptions;
            }

            return base.Set<TEntity>();
        }
    }

    private static Mock<DbSet<T>> BuildSet<T>(IReadOnlyList<T> items) where T : class
    {
        var data = items.AsQueryable();
        var set = new Mock<DbSet<T>>();
        set.As<IAsyncEnumerable<T>>()
            .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(() => new TestAsyncEnumerator<T>(data.GetEnumerator()));
        set.As<IQueryable<T>>().Setup(x => x.Provider).Returns(new TestAsyncQueryProvider<T>(data.Provider));
        set.As<IQueryable<T>>().Setup(x => x.Expression).Returns(data.Expression);
        set.As<IQueryable<T>>().Setup(x => x.ElementType).Returns(data.ElementType);
        set.As<IQueryable<T>>().Setup(x => x.GetEnumerator()).Returns(() => data.GetEnumerator());

        return set;
    }
}

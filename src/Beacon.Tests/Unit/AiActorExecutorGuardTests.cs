using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.Ai.AiActor.Models;
using Beacon.AI.Services.LlmProviders;
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

    private AiActorService BuildService(IReadOnlyList<Query>? queries = null, IReadOnlyList<Subscription>? subscriptions = null)
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
            Mock.Of<ILogger<AiActorService>>());
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

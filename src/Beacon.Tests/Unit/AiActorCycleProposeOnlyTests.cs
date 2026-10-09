using System.Text.Json;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.Ai.AiActor.Models;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using ActorEntity = Beacon.Core.Data.Entities.AiActor;

namespace Beacon.Tests.Unit;

/// <summary>
/// The three cycle loops — think, refine and initial setup — must route every LLM-planned action through the
/// propose-only seam: an actor that requires approval stores its actions in <c>ActionsJson</c> as proposals and
/// neither archives nor inserts anything. Driven end to end with a stubbed LLM over a mocked context (async-queryable
/// doubles, no database — §4.7); <see cref="IQueryService"/> / <see cref="ISubscriptionService"/> are strict, so any
/// executed archive fails the test, and the query / subscription sets record any insert.
/// </summary>
[TestFixture]
public class AiActorCycleProposeOnlyTests
{
    private const int ActorId = 5;
    private const int DataSourceId = 1;
    private const int OwnedQueryId = 1;

    // The actor's own unlocked query is the archive target, so an executed (not proposed) archive would reach
    // IQueryService.DeleteQuery rather than stop at the ownership guard.
    private const string PlanJson = """
        {
          "analysis": "one stale monitor, one gap",
          "findings": [],
          "actions": [
            { "actionType": "ARCHIVE_QUERY", "reasoning": "stale", "parameters": { "queryId": 1 } },
            { "actionType": "CREATE_QUERY", "reasoning": "gap", "parameters": { "name": "Failed payments", "sql": "SELECT 1" } }
          ],
          "shouldNotify": false
        }
        """;

    private const string MalformedPlanJson = """
        {
          "analysis": "malformed actions",
          "findings": [],
          "actions": [
            { "actionType": null, "reasoning": "no type", "parameters": { "queryId": 1 } },
            { "actionType": "ARCHIVE_QUERY", "reasoning": "no parameters", "parameters": null },
            null,
            { "actionType": "CREATE_QUERY", "reasoning": "gap", "parameters": { "name": "Failed payments", "sql": "SELECT 1" } }
          ],
          "shouldNotify": false
        }
        """;

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);

    private Store _store = null!;
    private Mock<DbSet<Query>> _queriesSet = null!;
    private Mock<DbSet<Subscription>> _subscriptionsSet = null!;
    private Mock<IQueryService> _queryService = null!;
    private Mock<ISubscriptionService> _subscriptionService = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new Store();
        _store.DataSources.Add(
            new DataSource
            {
                Id = DataSourceId,
                Name = "payments",
                DataSourceType = DataSourceType.Database,
                EncryptedConnectionData = "not-a-connection-string"
            });
        _store.Queries.Add(
            new Query
            {
                Id = OwnedQueryId,
                Name = "Stale monitor",
                AiActorId = ActorId
            });

        _queriesSet = BuildSet(_store.Queries);
        _subscriptionsSet = BuildSet(_store.Subscriptions);
        _queryService = new Mock<IQueryService>(MockBehavior.Strict);
        _subscriptionService = new Mock<ISubscriptionService>(MockBehavior.Strict);
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_RequiresApproval_StoresProposalsAndExecutesNothing()
    {
        var actor = SeedActor();
        var service = BuildService(PlanJson);

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        result.Success.Should().BeTrue();
        actor.Status.Should().Be(AiActorStatus.Active);
        AssertProposedAndNothingExecuted(SingleCompletedExecution());
    }

    [Test]
    public async Task RefineActorAsync_RequiresApproval_StoresProposalsAndExecutesNothing()
    {
        SeedActor();
        var service = BuildService(PlanJson);

        var result = await service.RefineActorAsync(ActorId, "also watch failed payments", CancellationToken.None);

        result.Success.Should().BeTrue();
        _store.Conversations.Should().HaveCount(2, "the feedback and the LLM reply are both stored");
        AssertProposedAndNothingExecuted(SingleCompletedExecution());
    }

    [Test]
    public async Task CreateActorAsync_InitialSetup_RequiresApprovalByDefault_StoresProposalsAndExecutesNothing()
    {
        var service = BuildService(PlanJson);

        var actor = await service.CreateActorAsync(
            new CreateAiActorOptions
            {
                Name = "Payments watcher",
                Instructions = "Watch failed payments",
                DataSourceId = DataSourceId,
                ActivateImmediately = true
            },
            CancellationToken.None);

        actor.RequiresApproval.Should().BeTrue("a new actor proposes until someone opts it out");
        actor.Status.Should().Be(AiActorStatus.Active, "initial setup must complete, not fail the actor");
        AssertProposedAndNothingExecuted(SingleCompletedExecution());
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_MalformedActions_FailOneByOneAndTheCycleCompletes()
    {
        var actor = SeedActor();
        var service = BuildService(MalformedPlanJson);

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        result.Success.Should().BeTrue();
        actor.Status.Should().Be(AiActorStatus.Active);
        actor.LastError.Should().BeNull();

        var actions = StoredActions(SingleCompletedExecution());
        actions.Should().HaveCount(4);
        actions.Take(3).Should().OnlyContain(x => !x.Proposed && !x.Success && !string.IsNullOrEmpty(x.ErrorMessage));
        actions[0].ErrorMessage.Should().Be("Unknown action type");
        actions[1].ErrorMessage.Should().Be("Unknown action type");
        actions[3].Proposed.Should().BeTrue("the actions after a malformed one are still processed");
        actions[3].ActionType.Should().Be(AiActorActionType.CreateQuery);
        AssertNothingExecuted();
    }

    private ActorEntity SeedActor()
    {
        var actor = new ActorEntity
        {
            Id = ActorId,
            Name = "Payments watcher",
            Instructions = "Watch failed payments",
            DataSourceId = DataSourceId,
            Status = AiActorStatus.Active,
            RequiresApproval = true
        };
        _store.Actors.Add(actor);

        return actor;
    }

    private AiActorExecution SingleCompletedExecution()
    {
        var execution = _store.Executions.Should().ContainSingle().Subject;
        execution.Phase.Should().Be(AiActorExecutionPhase.Completed, "the cycle must not fail (error: {0})", execution.ErrorMessage);

        return execution;
    }

    private void AssertProposedAndNothingExecuted(AiActorExecution execution)
    {
        var actions = StoredActions(execution);

        actions.Select(x => x.ActionType).Should().Equal(AiActorActionType.ArchiveQuery, AiActorActionType.CreateQuery);
        actions.Should().OnlyContain(x => x.Proposed && !x.Success);
        actions[0].TargetQueryId.Should().Be(OwnedQueryId);
        actions[1].QueryName.Should().Be("Failed payments");
        actions[1].SqlQuery.Should().Be("SELECT 1");
        execution.QueriesCreated.Should().Be(0);
        AssertNothingExecuted();
    }

    private void AssertNothingExecuted()
    {
        _queryService.VerifyNoOtherCalls();
        _subscriptionService.VerifyNoOtherCalls();
        _queriesSet.Verify(x => x.Add(It.IsAny<Query>()), Times.Never);
        _subscriptionsSet.Verify(x => x.Add(It.IsAny<Subscription>()), Times.Never);
        _store.Queries.Should().ContainSingle().Which.ArchivedTime.Should().BeNull();
    }

    private static List<AiActorAction> StoredActions(AiActorExecution execution)
    {
        execution.ActionsJson.Should().NotBeNullOrEmpty();

        return JsonSerializer.Deserialize<List<AiActorAction>>(execution.ActionsJson!, ReadOptions)!;
    }

    private AiActorService BuildService(string llmContent)
    {
        var sets = new Dictionary<Type, object>
        {
            [typeof(ActorEntity)] = BuildSet(_store.Actors).Object,
            [typeof(DataSource)] = BuildSet(_store.DataSources).Object,
            [typeof(Query)] = _queriesSet.Object,
            [typeof(Subscription)] = _subscriptionsSet.Object,
            [typeof(AiActorExecution)] = BuildSet(_store.Executions).Object,
            [typeof(AiActorConversation)] = BuildSet(_store.Conversations).Object
        };

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new CycleContext(sets));

        var llm = new Mock<ILlmProvider>(MockBehavior.Strict);
        llm
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new LlmResponse
                {
                    Content = llmContent,
                    Model = "stub"
                });

        var metadata = new Mock<IDatabaseMetadataService>(MockBehavior.Strict);
        metadata
            .Setup(x => x.GetMetadataAsync(DataSourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseMetadataSnapshot(DataSourceId, DatabaseEngineType.PostgreSQL, [], DateTime.UtcNow));

        return new AiActorService(
            factory.Object,
            llm.Object,
            metadata.Object,
            _queryService.Object,
            _subscriptionService.Object,
            Mock.Of<IBeaconScheduler>(MockBehavior.Strict),
            Mock.Of<ILogger<AiActorService>>());
    }

    private static Mock<DbSet<T>> BuildSet<T>(List<T> rows) where T : class
    {
        var data = rows.AsQueryable();
        var set = new Mock<DbSet<T>>();
        set.As<IAsyncEnumerable<T>>()
            .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(() => new TestAsyncEnumerator<T>(data.GetEnumerator()));
        set.As<IQueryable<T>>().Setup(x => x.Provider).Returns(new TestAsyncQueryProvider<T>(data.Provider));
        set.As<IQueryable<T>>().Setup(x => x.Expression).Returns(data.Expression);
        set.As<IQueryable<T>>().Setup(x => x.ElementType).Returns(data.ElementType);
        set.As<IQueryable<T>>().Setup(x => x.GetEnumerator()).Returns(() => data.GetEnumerator());
        set
            .Setup(x => x.Add(It.IsAny<T>()))
            .Callback<T>(x => rows.Add(x));

        return set;
    }

    /// <summary>The rows the cycle reads and the rows it adds; shared by every context the factory hands out.</summary>
    private sealed class Store
    {
        public List<ActorEntity> Actors { get; } = [];

        public List<DataSource> DataSources { get; } = [];

        public List<Query> Queries { get; } = [];

        public List<Subscription> Subscriptions { get; } = [];

        public List<AiActorExecution> Executions { get; } = [];

        public List<AiActorConversation> Conversations { get; } = [];
    }

    private sealed class CycleContext(IReadOnlyDictionary<Type, object> sets) : BeaconContext(Options, "beacon")
    {
        private static readonly DbContextOptions<CycleContext> Options =
            new DbContextOptionsBuilder<CycleContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (sets.TryGetValue(typeof(TEntity), out var set))
            {
                return (DbSet<TEntity>)set;
            }

            return base.Set<TEntity>();
        }

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}

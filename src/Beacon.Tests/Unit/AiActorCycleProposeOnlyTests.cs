using Beacon.Core.Authorization;
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
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Moq;
using NUnit.Framework;
using ActorEntity = Beacon.Core.Data.Entities.AiActor;
using Beacon.Core.SecurityAudit;

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
    private RecordingSecurityAudit _audit = null!;
    private Mock<ILogger<AiActorService>> _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = new Mock<ILogger<AiActorService>>();
        _audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(12, "ext-ben"));
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

    // A generated plan waits for a reviewer: its submission is on record (its actor, version and how many actions it
    // proposes), never its analysis or the SQL it proposes.
    [Test]
    public async Task GeneratePlanAsync_RecordsThePlansSubmission()
    {
        SeedActor();
        var service = BuildService(PlanJson);

        var result = await service.GeneratePlanAsync(new GeneratePlanOptions { ActorId = ActorId }, CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        var plan = _store.Plans.Should().ContainSingle().Subject;
        var submitted = _audit.Single("one stale monitor", "Failed payments", "SELECT 1");
        submitted.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.AiPlanSubmitted, ActorUserId = (int?)12, ResourceType = SecurityAuditResources.AiActorPlan, ResourceId = plan.Id.ToString() },
            x => x.ExcludingMissingMembers());
        submitted.DetailsJson.Should().Be("{\"aiActorId\":5,\"parentPlanId\":null,\"version\":1,\"proposedActionCount\":2}");
        _audit.Ways.Should().Equal("saved");
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
    public async Task ExecuteThinkCycleAsync_CancelledMidCycle_ClosesOnlyTheExecutionRowWithALiveTokenAndRethrows()
    {
        var actor = SeedActor();
        using var cancellation = new CancellationTokenSource();
        var service = BuildService(PlanJson, onLlmCall: () => cancellation.Cancel());

        var act = () => service.ExecuteThinkCycleAsync(ActorId, cancellationToken: cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        actor.Status.Should().Be(AiActorStatus.Active, "a shutdown must not stop the actor");
        actor.LastError.Should().BeNull();
        AssertCancelledExecution();
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_CancelledMidCycle_DoesNotWriteWhatTheCycleHadChangedButNotSaved()
    {
        var actor = SeedActor();
        using var cancellation = new CancellationTokenSource();
        var service = BuildService(
            PlanJson,
            onLlmCall: () =>
            {
                var context = _store.Contexts.First();
                context.Attach(actor).Property(x => x.LastError).CurrentValue = "unsaved";
                cancellation.Cancel();
            });

        var act = () => service.ExecuteThinkCycleAsync(ActorId, cancellationToken: cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _store.TrackedEntitiesAtSave.Last().Should().Be(0, "the unsaved change to the actor must not be written by the cancelled cycle");
    }

    [Test]
    public async Task RefineActorAsync_CancelledMidCycle_ClosesOnlyTheExecutionRowWithALiveTokenAndRethrows()
    {
        var actor = SeedActor();
        using var cancellation = new CancellationTokenSource();
        var service = BuildService(PlanJson, onLlmCall: () => cancellation.Cancel());

        var act = () => service.RefineActorAsync(ActorId, "also watch failed payments", cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        actor.Status.Should().Be(AiActorStatus.Active);
        AssertCancelledExecution();
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_RealFailure_StillSavesTheFailedStatusWithALiveToken()
    {
        var actor = SeedActor();
        var service = BuildService(PlanJson, onLlmCall: () => throw new InvalidOperationException("model unavailable"));

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("model unavailable");
        actor.Status.Should().Be(AiActorStatus.Failed);
        _store.Executions.Should().ContainSingle().Which.Phase.Should().Be(AiActorExecutionPhase.Failed);
        _store.SaveTokensCancelled.Last().Should().BeFalse();
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_WhenTheFailureSaveFails_KeepsTheOriginalFailure()
    {
        SeedActor();
        var service = BuildService(
            PlanJson,
            onLlmCall: () =>
            {
                _store.FailSaves = true;

                throw new InvalidOperationException("model unavailable");
            });

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("model unavailable");
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_CancelledWhenTheFailureSaveFails_RethrowsTheCancellation()
    {
        SeedActor();
        using var cancellation = new CancellationTokenSource();
        var service = BuildService(
            PlanJson,
            onLlmCall: () =>
            {
                _store.FailSaves = true;
                cancellation.Cancel();
            });

        var act = () => service.ExecuteThinkCycleAsync(ActorId, cancellationToken: cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
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
        actor.LastThinkTime.Should().BeNull("the initial setup does not start the think cooldown");
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

    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task ExecuteThinkCycleAsync_WithinCooldown_SkipsAndSaysHowLongIsLeft()
    {
        var actor = SeedActor();
        actor.LastThinkTime = Now.AddMinutes(-10);
        var llmCalls = 0;
        var service = BuildService(PlanJson, () => llmCalls++, time: new FixedTimeProvider(Now));

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain(AiActorService.ThinkCooldownMessage).And.Contain("5 minute(s)");
        llmCalls.Should().Be(0);
        _store.Executions.Should().BeEmpty();
        actor.LastThinkTime.Should().Be(Now.AddMinutes(-10));
    }

    [Test]
    public async Task ExecuteThinkCycleBackgroundAsync_WithinCooldown_SkipsAndLogsOnlyTheActorIdAtDebug()
    {
        var actor = SeedActor();
        actor.LastThinkTime = Now.AddMinutes(-1);
        var llmCalls = 0;
        var service = BuildService(PlanJson, () => llmCalls++, time: new FixedTimeProvider(Now));

        await service.ExecuteThinkCycleBackgroundAsync(ActorId, null, CancellationToken.None);

        llmCalls.Should().Be(0);
        _store.Executions.Should().BeEmpty();
        _logger.Invocations
            .Where(x => x.Arguments[0] is LogLevel.Debug)
            .Should()
            .ContainSingle(x => x.Arguments[2].ToString()!.Contains("within the think cooldown") && x.Arguments[2].ToString()!.Contains(ActorId.ToString()));
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_AfterCooldown_RunsAndRecordsTheLastThinkTime()
    {
        var actor = SeedActor();
        actor.LastThinkTime = Now.AddMinutes(-16);
        var service = BuildService(PlanJson, time: new FixedTimeProvider(Now));

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        actor.LastThinkTime.Should().Be(Now);
        actor.ThinkCount.Should().Be(1);
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_CooldownOff_RunsAgainImmediately()
    {
        var actor = SeedActor();
        actor.LastThinkTime = Now;
        var service = BuildService(PlanJson, time: new FixedTimeProvider(Now), cooldownMinutes: 0);

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_TwoCyclesAtOnce_OnlyTheClaimHolderRuns()
    {
        SeedActor();
        var llmCalls = 0;
        var claim = new SingleWinnerThinkClaim();
        var first = BuildService(PlanJson, () => llmCalls++, claim, new FixedTimeProvider(Now));
        var second = BuildService(PlanJson, () => llmCalls++, claim, new FixedTimeProvider(Now));

        var results = await Task.WhenAll(
            first.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None),
            second.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None));

        results.Count(x => x.Success).Should().Be(1);
        llmCalls.Should().Be(1);
        _store.Executions.Should().ContainSingle();
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_CooldownOff_DoesNotTouchTheClaim()
    {
        SeedActor();
        var claim = new Mock<IAiActorThinkClaim>(MockBehavior.Strict);
        var service = BuildService(PlanJson, claim: claim.Object, time: new FixedTimeProvider(Now), cooldownMinutes: 0);

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
    }

    [Test]
    public async Task ExecuteThinkCycleAsync_ActorArchivedBeforeTheClaim_SaysItIsUnavailable()
    {
        SeedActor();
        var claim = new Mock<IAiActorThinkClaim>();
        claim
            .Setup(x => x.TryClaimAsync(ActorId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ThinkClaimResult.Missing);
        var service = BuildService(PlanJson, claim: claim.Object, time: new FixedTimeProvider(Now));

        var result = await service.ExecuteThinkCycleAsync(ActorId, cancellationToken: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(AiActorService.ActorUnavailableMessage);
    }

    [Test]
    public void AiActorOptionsValidator_RejectsACooldownOutOfRange()
    {
        var validator = new AiActorOptionsValidator();

        validator.Validate(null, new AiActorOptions { ThinkCooldownMinutes = -1 }).Failed.Should().BeTrue();
        validator.Validate(null, new AiActorOptions { ThinkCooldownMinutes = AiActorOptions.MaxThinkCooldownMinutes + 1 }).Failed.Should().BeTrue();
        validator.Validate(null, new AiActorOptions { ThinkCooldownMinutes = 0 }).Succeeded.Should().BeTrue();
        validator.Validate(null, new AiActorOptions()).Succeeded.Should().BeTrue();
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class SingleWinnerThinkClaim : IAiActorThinkClaim
    {
        private int _taken;

        public Task<ThinkClaimResult> TryClaimAsync(int actorId, DateTime now, DateTime threshold, CancellationToken cancellationToken)
            => Task.FromResult(Interlocked.Exchange(ref _taken, 1) == 0 ? ThinkClaimResult.Claimed : ThinkClaimResult.Held);
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

    private void AssertCancelledExecution()
    {
        var execution = _store.Executions.Should().ContainSingle().Subject;
        execution.Phase.Should().Be(AiActorExecutionPhase.Failed);
        execution.ErrorMessage.Should().Be("cancelled");
        _store.SaveTokensCancelled.Should().NotBeEmpty();
        _store.SaveTokensCancelled.Last().Should().BeFalse("the failure record must not be written with the cancelled token");
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

    private AiActorService BuildService(
        string llmContent,
        Action? onLlmCall = null,
        IAiActorThinkClaim? claim = null,
        TimeProvider? time = null,
        int cooldownMinutes = 15)
    {
        var sets = new Dictionary<Type, object>
        {
            [typeof(ActorEntity)] = BuildSet(_store.Actors).Object,
            [typeof(DataSource)] = BuildSet(_store.DataSources).Object,
            [typeof(Query)] = _queriesSet.Object,
            [typeof(Subscription)] = _subscriptionsSet.Object,
            [typeof(AiActorExecution)] = BuildSet(_store.Executions).Object,
            [typeof(AiActorConversation)] = BuildSet(_store.Conversations).Object,
            [typeof(AiActorPlan)] = BuildSet(_store.Plans).Object
        };

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                () =>
                {
                    var context = new CycleContext(sets, _store);
                    _store.Contexts.Add(context);

                    return context;
                });

        var llm = new Mock<ILlmProvider>(MockBehavior.Strict);
        llm
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns<LlmRequest, CancellationToken>((_, token) =>
            {
                onLlmCall?.Invoke();
                token.ThrowIfCancellationRequested();

                return Task.FromResult(
                    new LlmResponse
                    {
                        Content = llmContent,
                        Model = "stub"
                    });
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
            Mock.Of<IRunAsScope>(),
            TestProjectAccess.ExecutionAllowed(),
            Mock.Of<IStepSqlSaveCheck>(),
            Mock.Of<IQueryProposalCheck>(MockBehavior.Strict),
            Mock.Of<IQueryVersionService>(),
            new Beacon.Core.BeaconConfiguration(),
            _audit,
            _logger.Object,
            Mock.Of<IOptionsMonitor<AiActorOptions>>(x => x.CurrentValue == new AiActorOptions { ThinkCooldownMinutes = cooldownMinutes }),
            time,
            claim ?? new GrantingThinkClaim());
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

        public List<AiActorPlan> Plans { get; } = [];

        public List<bool> SaveTokensCancelled { get; } = [];

        public bool FailSaves { get; set; }

        public List<BeaconContext> Contexts { get; } = [];

        public List<int> TrackedEntitiesAtSave { get; } = [];
    }

    private sealed class CycleContext(IReadOnlyDictionary<Type, object> sets, Store store) : BeaconContext(Options, "beacon")
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

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            store.SaveTokensCancelled.Add(cancellationToken.IsCancellationRequested);
            store.TrackedEntitiesAtSave.Add(ChangeTracker.Entries().Count());
            if (store.FailSaves)
            {
                throw new InvalidOperationException("database unavailable");
            }

            return Task.FromResult(0);
        }
    }
}

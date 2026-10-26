using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services;
using Beacon.Core.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Beacon.Tests.Common;

/// <summary>
/// A real <see cref="AiActorService"/> for the tests of what calls it (handlers, the think task), over rows served from
/// memory (no database, §4.7). It reads <see cref="Actors"/> (and the actors it adds), the plans <c>plans</c> serves
/// (each context its own list, every plan served kept in <see cref="ServedPlans"/>) and <see cref="DataSources"/>; what
/// it adds lands in <see cref="Added"/>. Its model calls are recorded in <see cref="LlmRequests"/> and answered with
/// <see cref="LlmContent"/>, or fail with <see cref="LlmFailure"/>. Every public member of the service opens a context
/// first, so <see cref="ContextsOpened"/> at zero proves a caller never reached it.
/// </summary>
internal sealed class TestAiActorService
{
    /// <summary>A model answer that proposes nothing: a cycle completes and changes nothing.</summary>
    public const string NoActionsJson = """{ "analysis": "all quiet", "findings": [], "actions": [], "shouldNotify": false }""";

    private readonly Func<List<AiActorPlan>> _plans;

    public TestAiActorService(List<AiActor> actors, Func<List<AiActorPlan>>? plans = null)
    {
        Actors = actors;
        _plans = plans ?? (() => []);
        Service = Build();
    }

    public AiActorService Service { get; }

    public List<AiActor> Actors { get; }

    public List<DataSource> DataSources { get; } = [];

    public List<object> Added { get; } = [];

    public List<AiActorPlan> ServedPlans { get; } = [];

    public List<LlmRequest> LlmRequests { get; } = [];

    public int ContextsOpened { get; private set; }

    public string LlmContent { get; set; } = NoActionsJson;

    public Exception? LlmFailure { get; set; }

    /// <summary>Whether the cycle's actor may still work with the actor's data source.</summary>
    public bool ExecutionAllowed { get; set; } = true;

    /// <summary>Whether the scope runs as an owner (a background cycle), so a refused cycle is skipped, not thrown.</summary>
    public bool RunsAsOwner { get; set; }

    public ThinkClaimResult ThinkClaim { get; set; } = ThinkClaimResult.Claimed;

    public RecordingSecurityAudit Audit { get; } = new();

    private AiActorService Build()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                ContextsOpened++;
                var plans = _plans();
                ServedPlans.AddRange(plans);

                return new RecordingBeaconContext(
                    new Dictionary<Type, object>
                    {
                        [typeof(AiActor)] = RecordingBeaconContext.MemorySet(Actors, Added, Actors.Concat(Added.OfType<AiActor>())),
                        [typeof(AiActorPlan)] = RecordingBeaconContext.MemorySet(plans, Added),
                        [typeof(AiActorExecution)] = RecordingBeaconContext.MemorySet(new List<AiActorExecution>(), Added),
                        [typeof(AiActorConversation)] = RecordingBeaconContext.MemorySet(new List<AiActorConversation>(), Added),
                        [typeof(DataSource)] = RecordingBeaconContext.MemorySet(DataSources, Added),
                        [typeof(Query)] = RecordingBeaconContext.MemorySet(new List<Query>(), Added),
                        [typeof(Subscription)] = RecordingBeaconContext.MemorySet(new List<Subscription>(), Added)
                    },
                    Added);
            });

        var llm = new Mock<ILlmProvider>(MockBehavior.Strict);
        llm
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns<LlmRequest, CancellationToken>((request, _) =>
            {
                LlmRequests.Add(request);
                if (LlmFailure != null)
                {
                    throw LlmFailure;
                }

                return Task.FromResult(new LlmResponse { Content = LlmContent, Model = "stub" });
            });

        var metadata = new Mock<IDatabaseMetadataService>(MockBehavior.Strict);
        metadata
            .Setup(x => x.GetMetadataAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int dataSourceId, CancellationToken _) => new DatabaseMetadataSnapshot(dataSourceId, DatabaseEngineType.PostgreSQL, [], DateTime.UtcNow));

        var executionAccess = new Mock<IExecutionAccessCheck>();
        executionAccess
            .Setup(x => x.AllowsAsync(It.IsAny<ProjectAction>(), It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ExecutionAllowed);

        var runAs = new Mock<IRunAsScope>();
        runAs
            .Setup(x => x.IsSet)
            .Returns(() => RunsAsOwner);

        var claim = new Mock<IAiActorThinkClaim>();
        claim
            .Setup(x => x.TryClaimAsync(It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ThinkClaim);

        return new AiActorService(
            factory.Object,
            llm.Object,
            metadata.Object,
            Mock.Of<IQueryService>(MockBehavior.Strict),
            Mock.Of<ISubscriptionService>(MockBehavior.Strict),
            Mock.Of<IBeaconScheduler>(MockBehavior.Strict),
            runAs.Object,
            executionAccess.Object,
            Mock.Of<IStepSqlSaveCheck>(MockBehavior.Strict),
            Mock.Of<IQueryProposalCheck>(MockBehavior.Strict),
            Mock.Of<IQueryVersionService>(MockBehavior.Strict),
            new BeaconConfiguration(),
            Audit,
            SubscriptionScheduleCheck.Default,
            NullLogger<AiActorService>.Instance,
            null,
            null,
            claim.Object);
    }
}

using Semantico.AI.Services.Ai.AiActor.Models;
using Semantico.Core.Models.Ai;

using Semantico.Core.Data.Entities;

namespace Semantico.AI.Services.Ai.AiActor;

/// <summary>
/// Extended AI Actor service interface with full functionality.
/// Extends the minimal Core.Services.IAiActorService interface.
/// </summary>
public interface IAiActorServiceExtended : Core.Services.IAiActorService
{
    Task<Core.Data.Entities.AiActor> CreateActorAsync(
        CreateAiActorOptions options,
        CancellationToken cancellationToken = default);

    Task<AiActorThinkResult> ExecuteThinkCycleAsync(
        int actorId,
        int? triggeringSubscriptionId = null,
        CancellationToken cancellationToken = default);

    Task ExecuteThinkCycleBackgroundAsync(int actorId, int? triggeringSubscriptionId = null);

    Task PauseActorAsync(int actorId, CancellationToken cancellationToken = default);

    Task ResumeActorAsync(int actorId, CancellationToken cancellationToken = default);

    Task ArchiveActorAsync(int actorId, CancellationToken cancellationToken = default);

    Task<AiActorThinkResult> RefineActorAsync(
        int actorId,
        string feedback,
        CancellationToken cancellationToken = default);

    Task<Core.Data.Entities.AiActor?> GetActorAsync(int actorId, CancellationToken cancellationToken = default);

    Task<List<Core.Data.Entities.AiActor>> GetActorsForDataSourceAsync(
        int dataSourceId,
        bool includeArchived = false,
        CancellationToken cancellationToken = default);

    Task<List<AiActorExecution>> GetExecutionHistoryAsync(
        int actorId,
        int? limit = null,
        CancellationToken cancellationToken = default);

    Task<List<Query>> GetActorQueriesAsync(
        int actorId,
        CancellationToken cancellationToken = default);

    Task<List<Subscription>> GetActorSubscriptionsAsync(
        int actorId,
        CancellationToken cancellationToken = default);

    Task<AiActorPlanResult> GeneratePlanAsync(
        GeneratePlanOptions options,
        CancellationToken cancellationToken = default);

    Task<AiActorThinkResult> ApprovePlanAsync(
        ApprovePlanOptions options,
        CancellationToken cancellationToken = default);

    Task RejectPlanAsync(
        RejectPlanOptions options,
        CancellationToken cancellationToken = default);

    Task<AiActorPlanResult> RequestPlanRevisionAsync(
        RequestRevisionOptions options,
        CancellationToken cancellationToken = default);

    Task<List<PendingPlanSummary>> GetPendingPlansAsync(
        int actorId,
        CancellationToken cancellationToken = default);

    Task<AiActorPlan?> GetPlanAsync(
        int planId,
        CancellationToken cancellationToken = default);
}

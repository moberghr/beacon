using Semantico.Core.Data.Entities;
using Semantico.Core.Services.Ai.AiActor.Models;

// Re-export plan types for convenience
using AiActorPlan = Semantico.Core.Data.Entities.AiActorPlan;

namespace Semantico.Core.Services.Ai.AiActor;

/// <summary>
/// Service for managing AI Actors that autonomously monitor data sources
/// </summary>
public interface IAiActorService
{
    /// <summary>
    /// Creates a new AI Actor for a data source
    /// </summary>
    Task<Data.Entities.AiActor> CreateActorAsync(
        CreateAiActorOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Called by JobService after a subscription executes - triggers the reactive think cycle
    /// </summary>
    Task OnSubscriptionExecutedAsync(
        int subscriptionId,
        int? rowCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a think cycle for an actor
    /// </summary>
    Task<AiActorThinkResult> ExecuteThinkCycleAsync(
        int actorId,
        int? triggeringSubscriptionId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Entry point for Hangfire background job execution
    /// </summary>
    Task ExecuteThinkCycleBackgroundAsync(
        int actorId,
        int? triggeringSubscriptionId = null);

    /// <summary>
    /// Pauses an active actor (stops reactive triggering)
    /// </summary>
    Task PauseActorAsync(
        int actorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes a paused actor
    /// </summary>
    Task ResumeActorAsync(
        int actorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Archives an actor (soft delete)
    /// </summary>
    Task ArchiveActorAsync(
        int actorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits user feedback to refine the actor's behavior
    /// </summary>
    Task<AiActorThinkResult> RefineActorAsync(
        int actorId,
        string feedback,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an actor by ID
    /// </summary>
    Task<Data.Entities.AiActor?> GetActorAsync(
        int actorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all actors for a data source
    /// </summary>
    Task<List<Data.Entities.AiActor>> GetActorsForDataSourceAsync(
        int dataSourceId,
        bool includeArchived = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets execution history for an actor
    /// </summary>
    Task<List<AiActorExecution>> GetExecutionHistoryAsync(
        int actorId,
        int? limit = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all queries created by an actor
    /// </summary>
    Task<List<Query>> GetActorQueriesAsync(
        int actorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all subscriptions created by an actor
    /// </summary>
    Task<List<Subscription>> GetActorSubscriptionsAsync(
        int actorId,
        CancellationToken cancellationToken = default);

    #region Plan Approval Workflow

    /// <summary>
    /// Generates a plan for user review without executing it.
    /// The plan can then be approved, rejected, or revised.
    /// </summary>
    Task<AiActorPlanResult> GeneratePlanAsync(
        GeneratePlanOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves a pending plan and executes it.
    /// </summary>
    Task<AiActorThinkResult> ApprovePlanAsync(
        ApprovePlanOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rejects a pending plan without executing it.
    /// </summary>
    Task RejectPlanAsync(
        RejectPlanOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests a revision to a pending plan with user feedback.
    /// This creates a new plan version.
    /// </summary>
    Task<AiActorPlanResult> RequestPlanRevisionAsync(
        RequestRevisionOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all pending plans for an actor.
    /// </summary>
    Task<List<PendingPlanSummary>> GetPendingPlansAsync(
        int actorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific plan by ID with full details.
    /// </summary>
    Task<AiActorPlan?> GetPlanAsync(
        int planId,
        CancellationToken cancellationToken = default);

    #endregion
}

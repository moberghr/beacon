using Semantico.Core.Data.Entities;
using Semantico.Core.Services.Ai.AiActor.Models;

namespace Semantico.Core.Services.Ai.AiActor;

/// <summary>
/// No-op implementation of IAiActorService for when AI features are disabled.
/// This allows JobService and other consumers to have the dependency satisfied
/// without requiring LLM configuration.
/// </summary>
internal sealed class NullAiActorService : IAiActorService
{
    public Task<Data.Entities.AiActor> CreateActorAsync(
        CreateAiActorOptions options,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("AI features are not enabled. Configure LLM settings in appsettings.json to use AI Actors.");
    }

    public Task OnSubscriptionExecutedAsync(
        int subscriptionId,
        int? rowCount,
        CancellationToken cancellationToken = default)
    {
        // No-op: AI actors are not enabled, so we don't need to trigger any think cycles
        return Task.CompletedTask;
    }

    public Task<AiActorThinkResult> ExecuteThinkCycleAsync(
        int actorId,
        int? triggeringSubscriptionId = null,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("AI features are not enabled. Configure LLM settings in appsettings.json to use AI Actors.");
    }

    public Task ExecuteThinkCycleBackgroundAsync(
        int actorId,
        int? triggeringSubscriptionId = null)
    {
        throw new InvalidOperationException("AI features are not enabled. Configure LLM settings in appsettings.json to use AI Actors.");
    }

    public Task PauseActorAsync(
        int actorId,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("AI features are not enabled. Configure LLM settings in appsettings.json to use AI Actors.");
    }

    public Task ResumeActorAsync(
        int actorId,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("AI features are not enabled. Configure LLM settings in appsettings.json to use AI Actors.");
    }

    public Task ArchiveActorAsync(
        int actorId,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("AI features are not enabled. Configure LLM settings in appsettings.json to use AI Actors.");
    }

    public Task<AiActorThinkResult> RefineActorAsync(
        int actorId,
        string feedback,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("AI features are not enabled. Configure LLM settings in appsettings.json to use AI Actors.");
    }

    public Task<Data.Entities.AiActor?> GetActorAsync(
        int actorId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<Data.Entities.AiActor?>(null);
    }

    public Task<List<Data.Entities.AiActor>> GetActorsForDataSourceAsync(
        int dataSourceId,
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new List<Data.Entities.AiActor>());
    }

    public Task<List<AiActorExecution>> GetExecutionHistoryAsync(
        int actorId,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new List<AiActorExecution>());
    }

    public Task<List<Query>> GetActorQueriesAsync(
        int actorId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new List<Query>());
    }

    public Task<List<Subscription>> GetActorSubscriptionsAsync(
        int actorId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new List<Subscription>());
    }

    public Task<AiActorPlanResult> GeneratePlanAsync(
        GeneratePlanOptions options,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("AI features are not enabled. Configure LLM settings in appsettings.json to use AI Actors.");
    }

    public Task<AiActorThinkResult> ApprovePlanAsync(
        ApprovePlanOptions options,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("AI features are not enabled. Configure LLM settings in appsettings.json to use AI Actors.");
    }

    public Task RejectPlanAsync(
        RejectPlanOptions options,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("AI features are not enabled. Configure LLM settings in appsettings.json to use AI Actors.");
    }

    public Task<AiActorPlanResult> RequestPlanRevisionAsync(
        RequestRevisionOptions options,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("AI features are not enabled. Configure LLM settings in appsettings.json to use AI Actors.");
    }

    public Task<List<PendingPlanSummary>> GetPendingPlansAsync(
        int actorId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new List<PendingPlanSummary>());
    }

    public Task<AiActorPlan?> GetPlanAsync(
        int planId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<AiActorPlan?>(null);
    }
}

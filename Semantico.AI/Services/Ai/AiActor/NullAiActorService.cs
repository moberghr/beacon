using Semantico.Core.Data.Entities;
using Semantico.AI.Services.Ai.AiActor.Models;
using Semantico.Core.Models.Ai;


namespace Semantico.AI.Services.Ai.AiActor;

/// <summary>
/// No-op implementation of IAiActorService for when AI features are disabled.
/// This allows JobService and other consumers to have the dependency satisfied
/// without requiring LLM configuration.
/// </summary>
internal sealed class NullAiActorService : IAiActorService
{
    private const string AiNotConfiguredMessage =
        "AI features are not enabled. To use AI Actors, configure LLM settings in appsettings.json:\n\n" +
        "{\n" +
        "  \"Semantico\": {\n" +
        "    \"LLM\": {\n" +
        "      \"Provider\": \"OpenAI\",\n" +
        "      \"ApiKey\": \"your-api-key\",\n" +
        "      \"Model\": \"gpt-4o\"\n" +
        "    }\n" +
        "  }\n" +
        "}";

    public Task<Semantico.Core.Data.Entities.AiActor> CreateActorAsync(
        CreateAiActorOptions options,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(AiNotConfiguredMessage);
    }

    public Task OnSubscriptionExecutedAsync(
        int subscriptionId,
        int rowCount,
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
        throw new InvalidOperationException(AiNotConfiguredMessage);
    }

    public Task ExecuteThinkCycleBackgroundAsync(
        int actorId,
        int? triggeringSubscriptionId = null)
    {
        throw new InvalidOperationException(AiNotConfiguredMessage);
    }

    public Task PauseActorAsync(
        int actorId,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(AiNotConfiguredMessage);
    }

    public Task ResumeActorAsync(
        int actorId,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(AiNotConfiguredMessage);
    }

    public Task ArchiveActorAsync(
        int actorId,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(AiNotConfiguredMessage);
    }

    public Task<AiActorThinkResult> RefineActorAsync(
        int actorId,
        string feedback,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(AiNotConfiguredMessage);
    }

    public Task<Semantico.Core.Data.Entities.AiActor?> GetActorAsync(
        int actorId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<Semantico.Core.Data.Entities.AiActor?>(null);
    }

    public Task<List<Semantico.Core.Data.Entities.AiActor>> GetActorsForDataSourceAsync(
        int dataSourceId,
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new List<Semantico.Core.Data.Entities.AiActor>());
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
        throw new InvalidOperationException(AiNotConfiguredMessage);
    }

    public Task<AiActorThinkResult> ApprovePlanAsync(
        ApprovePlanOptions options,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(AiNotConfiguredMessage);
    }

    public Task RejectPlanAsync(
        RejectPlanOptions options,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(AiNotConfiguredMessage);
    }

    public Task<AiActorPlanResult> RequestPlanRevisionAsync(
        RequestRevisionOptions options,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(AiNotConfiguredMessage);
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

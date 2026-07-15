using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Worker;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Queries;
using Beacon.Core.Models.Subscriptions;
using Beacon.AI.Services.Ai.AiActor.Models;
using Beacon.Core.Models.Ai;

using Beacon.AI.Services.LlmProviders;

namespace Beacon.AI.Services.Ai.AiActor;

/// <summary>
/// Service for managing AI Actors that autonomously monitor data sources
/// </summary>
public class AiActorService : IAiActorServiceExtended
{
    private readonly IDbContextFactory<BeaconContext> _contextFactory;
    private readonly ILlmProvider _llmProvider;
    private readonly IDatabaseMetadataService _metadataService;
    private readonly IQueryService _queryService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IBeaconScheduler _beaconScheduler;
    private readonly ILogger<AiActorService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AiActorService(
        IDbContextFactory<BeaconContext> contextFactory,
        ILlmProvider llmProvider,
        IDatabaseMetadataService metadataService,
        IQueryService queryService,
        ISubscriptionService subscriptionService,
        IBeaconScheduler beaconScheduler,
        ILogger<AiActorService> logger)
    {
        _contextFactory = contextFactory;
        _llmProvider = llmProvider;
        _metadataService = metadataService;
        _queryService = queryService;
        _subscriptionService = subscriptionService;
        _beaconScheduler = beaconScheduler;
        _logger = logger;
    }

    public async Task<Beacon.Core.Data.Entities.AiActor> CreateActorAsync(
        CreateAiActorOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Verify data source exists
        var dataSource = await context.DataSources
            .Where(ds => ds.Id == options.DataSourceId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconException($"DataSource with ID {options.DataSourceId} not found");

        var actor = new Beacon.Core.Data.Entities.AiActor
        {
            Name = options.Name,
            Instructions = options.Instructions,
            AdditionalContext = options.AdditionalContext,
            DataSourceId = options.DataSourceId,
            Status = options.ActivateImmediately ? AiActorStatus.Active : AiActorStatus.Draft,
            MaxQueries = options.MaxQueries,
            MaxSubscriptionsPerQuery = options.MaxSubscriptionsPerQuery,
            CreatedByUserId = options.CreatedByUserId
        };

        context.AiActors.Add(actor);
        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created AI Actor {ActorId} '{ActorName}' for DataSource {DataSourceId}",
            actor.Id, actor.Name, actor.DataSourceId);

        // If activated immediately, run initial setup
        if (options.ActivateImmediately)
        {
            try
            {
                await ExecuteInitialSetupAsync(actor.Id, options.DefaultRecipientIds, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to run initial setup for actor {ActorId}", actor.Id);
                actor.Status = AiActorStatus.Failed;
                actor.LastError = ex.Message;
                await context.SaveChangesAsync(cancellationToken);
            }
        }

        return actor;
    }

    public async Task OnSubscriptionExecutedAsync(
        int subscriptionId,
        int rowCount,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Check if this subscription belongs to an active actor. Project only the
        // fields we need so we don't materialize the full Subscription + AiActor graph.
        var subscriptionInfo = await context.Subscriptions
            .AsNoTracking()
            .Where(x => x.Id == subscriptionId)
            .Where(x => x.AiActorId != null)
            .Select(x =>
                new
                {
                    x.AiActorId,
                    ActorStatus = (AiActorStatus?)x.AiActor!.Status
                })
            .FirstOrDefaultAsync(cancellationToken);

        if (subscriptionInfo?.AiActorId == null || subscriptionInfo.ActorStatus == null)
        {
            return;
        }

        if (subscriptionInfo.ActorStatus != AiActorStatus.Active)
        {
            _logger.LogDebug("Skipping think cycle for actor {ActorId} - status is {Status}",
                subscriptionInfo.AiActorId, subscriptionInfo.ActorStatus);
            return;
        }

        var actorId = subscriptionInfo.AiActorId.Value;

        // Enqueue the think cycle as a background job so the subscription-execution
        // pipeline does not block on LLM round-trips. The job runs under its own scope
        // and its own CancellationToken.
        var jobId = await _beaconScheduler.EnqueueAiActorThinkCycle(actorId, subscriptionId);

        _logger.LogInformation(
            "Subscription {SubscriptionId} executed, enqueued think cycle job {JobId} for actor {ActorId}",
            subscriptionId, jobId, actorId);
    }

    public async Task<AiActorThinkResult> ExecuteThinkCycleAsync(
        int actorId,
        int? triggeringSubscriptionId = null,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var actor = await context.AiActors
            .Include(a => a.DataSource)
            .Where(a => a.Id == actorId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconException($"AI Actor with ID {actorId} not found");

        if (actor.Status != AiActorStatus.Active)
        {
            throw new BeaconException($"AI Actor {actorId} is not active (status: {actor.Status})");
        }

        // SaveChanges checkpoints in this method are deliberate:
        //   1. After creating the execution row (need the generated Id for downstream refs).
        //   2. Before/after each external LLM call (so the row's Phase reflects reality if
        //      the LLM call hangs, throws, or the worker dies mid-cycle).
        //   3. Final save persists action metrics + actor counters atomically.
        // Any consecutive saves with no external observation between them are collapsed.

        // Create execution record (Phase already Analyzing — no second save needed).
        var execution = new AiActorExecution
        {
            AiActorId = actorId,
            TriggeringSubscriptionId = triggeringSubscriptionId,
            Phase = AiActorExecutionPhase.Analyzing,
            StartedAt = startTime
        };
        context.AiActorExecutions.Add(execution);
        await context.SaveChangesAsync(cancellationToken);

        try
        {
            // 1. ANALYZING PHASE (already persisted above).
            var schemaContext = await GetSchemaContextAsync(actor.DataSourceId, cancellationToken);
            var existingQueries = await GetQueryContextAsync(actorId, cancellationToken);
            var recentResults = triggeringSubscriptionId.HasValue
                ? await GetRecentResultsAsync(triggeringSubscriptionId.Value, cancellationToken)
                : null;

            execution.QueriesAnalyzed = existingQueries.Count;

            // 2. PLANNING PHASE — checkpoint before the LLM call so a hang/crash leaves a trail.
            execution.Phase = AiActorExecutionPhase.Planning;
            await context.SaveChangesAsync(cancellationToken);

            var userPrompt = AiActorPrompts.BuildThinkCyclePrompt(
                actor, schemaContext, existingQueries, triggeringSubscriptionId, recentResults);

            var llmRequest = new LlmRequest
            {
                SystemPrompt = AiActorPrompts.ThinkCycleSystemPrompt,
                Messages = new List<ChatMessage>
                {
                    new(ConversationRole.User, userPrompt)
                },
                Temperature = 0.3m,
                MaxTokens = 4096
            };

            var llmResponse = await _llmProvider.CompleteAsync(llmRequest, cancellationToken);

            execution.TokensUsed = llmResponse.TotalTokens;
            execution.EstimatedCost = llmResponse.Cost;
            execution.Model = llmResponse.Model;

            // Parse LLM response
            var planResponse = ParseLlmResponse(llmResponse.Content);
            execution.DecisionSummary = planResponse.Analysis;

            // 3. EXECUTING PHASE
            execution.Phase = AiActorExecutionPhase.Executing;
            await context.SaveChangesAsync(cancellationToken);

            var executedActions = new List<AiActorAction>();
            var newlyCreatedQueries = new Dictionary<string, int>(); // Map query names to IDs

            foreach (var actionPlan in planResponse.Actions)
            {
                var action = await ExecuteActionAsync(
                    actor, actionPlan, newlyCreatedQueries, cancellationToken);
                executedActions.Add(action);

                // Track newly created query IDs
                if (action.ActionType == AiActorActionType.CreateQuery &&
                    action.Success && action.ResultEntityId.HasValue &&
                    !string.IsNullOrEmpty(action.QueryName))
                {
                    newlyCreatedQueries[action.QueryName] = action.ResultEntityId.Value;
                }

                // Update execution metrics
                switch (action.ActionType)
                {
                    case AiActorActionType.CreateQuery when action.Success:
                        execution.QueriesCreated++;
                        break;
                    case AiActorActionType.RefineQuery when action.Success:
                        execution.QueriesRefined++;
                        break;
                    case AiActorActionType.CreateSubscription when action.Success:
                        execution.SubscriptionsCreated++;
                        break;
                }
            }

            execution.ActionsJson = JsonSerializer.Serialize(executedActions, JsonOptions);

            // 4. NOTIFYING PHASE (if needed)
            if (planResponse.ShouldNotify && !string.IsNullOrEmpty(planResponse.NotificationReason))
            {
                execution.Phase = AiActorExecutionPhase.Notifying;
                await context.SaveChangesAsync(cancellationToken);

                // Recipient fan-out from the AI Actor planner is owned by the
                // subscription notification pipeline (JobService); the planner only
                // records intent here. NotificationsTriggered counts the planner's
                // intent so dashboards can show "wants to notify" without
                // double-counting the subscription pipeline's own deliveries.
                execution.NotificationsTriggered = 1;
                _logger.LogInformation("Actor {ActorId} wants to notify: {Reason}",
                    actorId, planResponse.NotificationReason);
            }

            // 5. COMPLETE
            execution.Phase = AiActorExecutionPhase.Completed;
            execution.CompletedAt = DateTime.UtcNow;

            // Update actor statistics
            actor.LastThinkTime = DateTime.UtcNow;
            actor.ThinkCount++;
            actor.TotalTokensUsed += execution.TokensUsed;
            actor.TotalCost += execution.EstimatedCost;

            await context.SaveChangesAsync(cancellationToken);

            var result = AiActorThinkResult.CreateSuccess(execution.Id, execution.DecisionSummary);
            result.Findings = planResponse.Findings;
            result.Actions = executedActions;
            result.QueriesAnalyzed = execution.QueriesAnalyzed;
            result.QueriesCreated = execution.QueriesCreated;
            result.QueriesRefined = execution.QueriesRefined;
            result.SubscriptionsCreated = execution.SubscriptionsCreated;
            result.NotificationsTriggered = execution.NotificationsTriggered;
            result.TokensUsed = execution.TokensUsed;
            result.EstimatedCost = execution.EstimatedCost;
            result.Duration = execution.CompletedAt.Value - execution.StartedAt;

            return result;
        }
        catch (Exception ex)
        {
            execution.Phase = AiActorExecutionPhase.Failed;
            execution.ErrorMessage = ex.Message;
            execution.CompletedAt = DateTime.UtcNow;

            actor.Status = AiActorStatus.Failed;
            actor.LastError = ex.Message;

            await context.SaveChangesAsync(cancellationToken);

            _logger.LogError(ex, "Think cycle failed for actor {ActorId}", actorId);

            return AiActorThinkResult.CreateFailure(execution.Id, ex.Message);
        }
    }

    public async Task ExecuteThinkCycleBackgroundAsync(int actorId, int? triggeringSubscriptionId = null)
    {
        await ExecuteThinkCycleAsync(actorId, triggeringSubscriptionId);
    }

    public async Task PauseActorAsync(int actorId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var actor = await context.AiActors
            .Where(a => a.Id == actorId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconException($"AI Actor with ID {actorId} not found");

        if (actor.Status != AiActorStatus.Active)
        {
            throw new BeaconException($"Cannot pause actor with status {actor.Status}");
        }

        actor.Status = AiActorStatus.Paused;
        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Paused AI Actor {ActorId}", actorId);
    }

    public async Task ResumeActorAsync(int actorId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var actor = await context.AiActors
            .Where(a => a.Id == actorId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconException($"AI Actor with ID {actorId} not found");

        if (actor.Status != AiActorStatus.Paused && actor.Status != AiActorStatus.Failed)
        {
            throw new BeaconException($"Cannot resume actor with status {actor.Status}");
        }

        actor.Status = AiActorStatus.Active;
        actor.LastError = null;
        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Resumed AI Actor {ActorId}", actorId);
    }

    public async Task ArchiveActorAsync(int actorId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var actor = await context.AiActors
            .Where(a => a.Id == actorId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconException($"AI Actor with ID {actorId} not found");

        actor.Status = AiActorStatus.Archived;
        actor.ArchivedTime = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Archived AI Actor {ActorId}", actorId);
    }

    public async Task<AiActorThinkResult> RefineActorAsync(
        int actorId,
        string feedback,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var actor = await context.AiActors
            .Include(a => a.DataSource)
            .Where(a => a.Id == actorId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconException($"AI Actor with ID {actorId} not found");

        // Create execution record
        var execution = new AiActorExecution
        {
            AiActorId = actorId,
            Phase = AiActorExecutionPhase.Analyzing,
            StartedAt = startTime
        };
        context.AiActorExecutions.Add(execution);

        // Store the feedback in conversation history
        var conversation = new AiActorConversation
        {
            AiActorId = actorId,
            Role = ConversationRole.User,
            MessageContent = feedback,
            Timestamp = startTime,
            TurnNumber = await GetNextConversationTurnAsync(actorId, cancellationToken)
        };
        context.AiActorConversations.Add(conversation);
        await context.SaveChangesAsync(cancellationToken);

        try
        {
            var schemaContext = await GetSchemaContextAsync(actor.DataSourceId, cancellationToken);
            var existingQueries = await GetQueryContextAsync(actorId, cancellationToken);

            var userPrompt = AiActorPrompts.BuildRefinementPrompt(
                actor, schemaContext, existingQueries, feedback);

            var llmRequest = new LlmRequest
            {
                SystemPrompt = AiActorPrompts.RefinementSystemPrompt,
                Messages = new List<ChatMessage>
                {
                    new(ConversationRole.User, userPrompt)
                },
                Temperature = 0.3m,
                MaxTokens = 4096
            };

            var llmResponse = await _llmProvider.CompleteAsync(llmRequest, cancellationToken);

            // Store assistant response
            var assistantConversation = new AiActorConversation
            {
                AiActorId = actorId,
                AiActorExecutionId = execution.Id,
                Role = ConversationRole.Assistant,
                MessageContent = llmResponse.Content,
                TokensUsed = llmResponse.TotalTokens,
                Model = llmResponse.Model,
                Timestamp = DateTime.UtcNow,
                TurnNumber = conversation.TurnNumber + 1
            };
            context.AiActorConversations.Add(assistantConversation);

            execution.TokensUsed = llmResponse.TotalTokens;
            execution.EstimatedCost = llmResponse.Cost;
            execution.Model = llmResponse.Model;

            var planResponse = ParseLlmResponse(llmResponse.Content);
            execution.DecisionSummary = planResponse.Analysis;

            // Execute actions
            execution.Phase = AiActorExecutionPhase.Executing;
            var executedActions = new List<AiActorAction>();
            var newlyCreatedQueries = new Dictionary<string, int>();

            foreach (var actionPlan in planResponse.Actions)
            {
                var action = await ExecuteActionAsync(actor, actionPlan, newlyCreatedQueries, cancellationToken);
                executedActions.Add(action);

                if (action.ActionType == AiActorActionType.CreateQuery &&
                    action.Success && action.ResultEntityId.HasValue &&
                    !string.IsNullOrEmpty(action.QueryName))
                {
                    newlyCreatedQueries[action.QueryName] = action.ResultEntityId.Value;
                }

                switch (action.ActionType)
                {
                    case AiActorActionType.CreateQuery when action.Success:
                        execution.QueriesCreated++;
                        break;
                    case AiActorActionType.RefineQuery when action.Success:
                        execution.QueriesRefined++;
                        break;
                    case AiActorActionType.CreateSubscription when action.Success:
                        execution.SubscriptionsCreated++;
                        break;
                }
            }

            execution.ActionsJson = JsonSerializer.Serialize(executedActions, JsonOptions);
            execution.Phase = AiActorExecutionPhase.Completed;
            execution.CompletedAt = DateTime.UtcNow;

            actor.TotalTokensUsed += execution.TokensUsed;
            actor.TotalCost += execution.EstimatedCost;

            await context.SaveChangesAsync(cancellationToken);

            var result = AiActorThinkResult.CreateSuccess(execution.Id, execution.DecisionSummary);
            result.Findings = planResponse.Findings;
            result.Actions = executedActions;
            result.QueriesCreated = execution.QueriesCreated;
            result.QueriesRefined = execution.QueriesRefined;
            result.SubscriptionsCreated = execution.SubscriptionsCreated;
            result.TokensUsed = execution.TokensUsed;
            result.EstimatedCost = execution.EstimatedCost;
            result.Duration = execution.CompletedAt.Value - execution.StartedAt;

            return result;
        }
        catch (Exception ex)
        {
            execution.Phase = AiActorExecutionPhase.Failed;
            execution.ErrorMessage = ex.Message;
            execution.CompletedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);

            _logger.LogError(ex, "Refinement failed for actor {ActorId}", actorId);
            return AiActorThinkResult.CreateFailure(execution.Id, ex.Message);
        }
    }

    public async Task<Beacon.Core.Data.Entities.AiActor?> GetActorAsync(
        int actorId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.AiActors
            .AsNoTracking()
            .Where(x => x.Id == actorId)
            .Select(x =>
                new Beacon.Core.Data.Entities.AiActor
                {
                    Id = x.Id,
                    Name = x.Name,
                    Instructions = x.Instructions,
                    AdditionalContext = x.AdditionalContext,
                    DataSourceId = x.DataSourceId,
                    Status = x.Status,
                    MaxQueries = x.MaxQueries,
                    MaxSubscriptionsPerQuery = x.MaxSubscriptionsPerQuery,
                    RequiresApproval = x.RequiresApproval,
                    CreatedByUserId = x.CreatedByUserId,
                    TotalTokensUsed = x.TotalTokensUsed,
                    TotalCost = x.TotalCost,
                    LastThinkTime = x.LastThinkTime,
                    ThinkCount = x.ThinkCount,
                    LastError = x.LastError,
                    CreatedTime = x.CreatedTime,
                    ArchivedTime = x.ArchivedTime,
                    DataSource = new DataSource
                    {
                        Id = x.DataSource.Id,
                        Name = x.DataSource.Name,
                        DataSourceType = x.DataSource.DataSourceType,
                        EncryptedConnectionData = string.Empty
                    }
                })
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<List<Beacon.Core.Data.Entities.AiActor>> GetActorsForDataSourceAsync(
        int? dataSourceId,
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.AiActors
            .AsNoTracking()
            .AsQueryable();

        if (dataSourceId.HasValue)
        {
            query = query.Where(x => x.DataSourceId == dataSourceId.Value);
        }

        // The global soft-delete filter excludes archived actors by default; opt back in explicitly.
        if (includeArchived)
        {
            query = query.IgnoreQueryFilters();
        }

        return await query
            .OrderByDescending(x => x.CreatedTime)
            .Select(x =>
                new Beacon.Core.Data.Entities.AiActor
                {
                    Id = x.Id,
                    Name = x.Name,
                    Instructions = x.Instructions,
                    AdditionalContext = x.AdditionalContext,
                    DataSourceId = x.DataSourceId,
                    Status = x.Status,
                    MaxQueries = x.MaxQueries,
                    MaxSubscriptionsPerQuery = x.MaxSubscriptionsPerQuery,
                    RequiresApproval = x.RequiresApproval,
                    CreatedByUserId = x.CreatedByUserId,
                    TotalTokensUsed = x.TotalTokensUsed,
                    TotalCost = x.TotalCost,
                    LastThinkTime = x.LastThinkTime,
                    ThinkCount = x.ThinkCount,
                    LastError = x.LastError,
                    CreatedTime = x.CreatedTime,
                    ArchivedTime = x.ArchivedTime,
                    DataSource = new DataSource
                    {
                        Id = x.DataSource.Id,
                        Name = x.DataSource.Name,
                        DataSourceType = x.DataSource.DataSourceType,
                        EncryptedConnectionData = string.Empty
                    }
                })
            .ToListAsync(cancellationToken);
    }

    public async Task<List<AiActorExecution>> GetExecutionHistoryAsync(
        int actorId,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.AiActorExecutions
            .Where(e => e.AiActorId == actorId)
            .OrderByDescending(e => e.StartedAt);

        if (limit.HasValue)
        {
            return await query.Take(limit.Value).ToListAsync(cancellationToken);
        }

        return await query.ToListAsync(cancellationToken);
    }

    public async Task<List<Query>> GetActorQueriesAsync(
        int actorId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Queries
            .AsNoTracking()
            .Where(x => x.AiActorId == actorId)
            .OrderByDescending(x => x.CreatedTime)
            .Select(x =>
                new Query
                {
                    Id = x.Id,
                    Name = x.Name,
                    Description = x.Description,
                    AiActorId = x.AiActorId,
                    IsLocked = x.IsLocked,
                    LockedAt = x.LockedAt,
                    CreatedTime = x.CreatedTime,
                    Subscriptions = x.Subscriptions
                        .Select(y =>
                            new Subscription
                            {
                                Id = y.Id,
                                QueryId = y.QueryId,
                                CronExpression = y.CronExpression
                            })
                        .ToList()
                })
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Subscription>> GetActorSubscriptionsAsync(
        int actorId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Subscriptions
            .AsNoTracking()
            .Where(x => x.AiActorId == actorId)
            .OrderByDescending(x => x.CreatedTime)
            .Select(x =>
                new Subscription
                {
                    Id = x.Id,
                    QueryId = x.QueryId,
                    AiActorId = x.AiActorId,
                    CronExpression = x.CronExpression,
                    NotificationTrigger = x.NotificationTrigger,
                    CreatedTime = x.CreatedTime,
                    Query = new Query
                    {
                        Id = x.Query.Id,
                        Name = x.Query.Name
                    }
                })
            .ToListAsync(cancellationToken);
    }

    #region Private Helper Methods

    private async Task ExecuteInitialSetupAsync(
        int actorId,
        List<int>? defaultRecipientIds,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var actor = await context.AiActors
            .Include(a => a.DataSource)
            .Where(a => a.Id == actorId)
            .FirstAsync(cancellationToken);

        var execution = new AiActorExecution
        {
            AiActorId = actorId,
            Phase = AiActorExecutionPhase.Analyzing,
            StartedAt = DateTime.UtcNow
        };
        context.AiActorExecutions.Add(execution);
        await context.SaveChangesAsync(cancellationToken);

        try
        {
            var schemaContext = await GetSchemaContextAsync(actor.DataSourceId, cancellationToken);

            var userPrompt = AiActorPrompts.BuildInitialSetupPrompt(actor, schemaContext);

            var llmRequest = new LlmRequest
            {
                SystemPrompt = AiActorPrompts.InitialSetupSystemPrompt,
                Messages = new List<ChatMessage>
                {
                    new(ConversationRole.User, userPrompt)
                },
                Temperature = 0.3m,
                MaxTokens = 4096
            };

            execution.Phase = AiActorExecutionPhase.Planning;
            await context.SaveChangesAsync(cancellationToken);

            var llmResponse = await _llmProvider.CompleteAsync(llmRequest, cancellationToken);

            execution.TokensUsed = llmResponse.TotalTokens;
            execution.EstimatedCost = llmResponse.Cost;
            execution.Model = llmResponse.Model;

            var planResponse = ParseLlmResponse(llmResponse.Content);
            execution.DecisionSummary = planResponse.Analysis;

            execution.Phase = AiActorExecutionPhase.Executing;
            await context.SaveChangesAsync(cancellationToken);

            var executedActions = new List<AiActorAction>();
            var newlyCreatedQueries = new Dictionary<string, int>();

            foreach (var actionPlan in planResponse.Actions)
            {
                var action = await ExecuteActionAsync(actor, actionPlan, newlyCreatedQueries, cancellationToken);
                executedActions.Add(action);

                if (action.ActionType == AiActorActionType.CreateQuery &&
                    action.Success && action.ResultEntityId.HasValue &&
                    !string.IsNullOrEmpty(action.QueryName))
                {
                    newlyCreatedQueries[action.QueryName] = action.ResultEntityId.Value;
                }

                switch (action.ActionType)
                {
                    case AiActorActionType.CreateQuery when action.Success:
                        execution.QueriesCreated++;
                        break;
                    case AiActorActionType.CreateSubscription when action.Success:
                        execution.SubscriptionsCreated++;
                        break;
                }
            }

            execution.ActionsJson = JsonSerializer.Serialize(executedActions, JsonOptions);
            execution.Phase = AiActorExecutionPhase.Completed;
            execution.CompletedAt = DateTime.UtcNow;

            actor.LastThinkTime = DateTime.UtcNow;
            actor.ThinkCount++;
            actor.TotalTokensUsed += execution.TokensUsed;
            actor.TotalCost += execution.EstimatedCost;

            await context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Initial setup completed for actor {ActorId}: {QueriesCreated} queries, {SubscriptionsCreated} subscriptions",
                actorId, execution.QueriesCreated, execution.SubscriptionsCreated);
        }
        catch (Exception ex)
        {
            execution.Phase = AiActorExecutionPhase.Failed;
            execution.ErrorMessage = ex.Message;
            execution.CompletedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    private async Task<string> GetSchemaContextAsync(int dataSourceId, CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await _metadataService.GetMetadataAsync(dataSourceId, cancellationToken);

            var tables = metadata.Tables.Select(t => new AiActorPrompts.TableSchemaInfo
            {
                SchemaName = t.SchemaName,
                TableName = t.TableName,
                Columns = t.Columns.Select(c => new AiActorPrompts.ColumnSchemaInfo
                {
                    Name = c.ColumnName,
                    DataType = c.DataType,
                    IsPrimaryKey = c.IsPrimaryKey,
                    IsForeignKey = c.IsForeignKey,
                    IsNullable = c.IsNullable,
                    ForeignKeyReference = c.IsForeignKey ? $"{c.ForeignKeyTable}.{c.ForeignKeyColumn}" : null
                }).ToList()
            }).ToList();

            return AiActorPrompts.FormatSchemaContext(tables);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch schema metadata for data source {DataSourceId}", dataSourceId);
            return "Schema metadata not available.";
        }
    }

    private async Task<List<AiActorPrompts.QueryContext>> GetQueryContextAsync(
        int actorId,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var queries = await context.Queries
            .Where(q => q.AiActorId == actorId)
            .Include(q => q.Subscriptions)
                .ThenInclude(s => s.QueryExecutionHistory)
            .Include(q => q.Steps)
            .ToListAsync(cancellationToken);

        return queries.Select(q => new AiActorPrompts.QueryContext
        {
            QueryId = q.Id,
            QueryName = q.Name,
            Sql = q.Steps.OrderBy(s => s.StepOrder).FirstOrDefault()?.SqlValue ?? q.FinalQuery ?? "",
            Description = q.Description,
            Subscriptions = q.Subscriptions.Select(s =>
            {
                var latestExecution = s.QueryExecutionHistory?
                    .OrderByDescending(h => h.CreatedTime)
                    .FirstOrDefault();
                return new AiActorPrompts.SubscriptionContext
                {
                    SubscriptionId = s.Id,
                    CronExpression = s.CronExpression,
                    NotificationTrigger = s.NotificationTrigger.ToString(),
                    LastExecutionTime = latestExecution?.CreatedTime,
                    LastResultCount = latestExecution?.ResultCount
                };
            }).ToList()
        }).ToList();
    }

    private async Task<string?> GetRecentResultsAsync(int subscriptionId, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var recentHistory = await context.QueryExecutionHistory
            .Where(h => h.SubscriptionId == subscriptionId)
            .OrderByDescending(h => h.CreatedTime)
            .Take(5)
            .Select(h => new { h.CreatedTime, h.ResultCount, h.ExecutionTimeMs })
            .ToListAsync(cancellationToken);

        if (!recentHistory.Any())
            return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Recent executions:");
        foreach (var h in recentHistory)
        {
            sb.AppendLine($"- {h.CreatedTime:yyyy-MM-dd HH:mm}: {h.ResultCount} rows ({h.ExecutionTimeMs}ms)");
        }

        return sb.ToString();
    }

    private AiActorPlanResponse ParseLlmResponse(string content)
    {
        try
        {
            // Try to extract JSON from the response (LLM might include explanatory text)
            var jsonStart = content.IndexOf('{');
            var jsonEnd = content.LastIndexOf('}');

            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonContent = content.Substring(jsonStart, jsonEnd - jsonStart + 1);
                return JsonSerializer.Deserialize<AiActorPlanResponse>(jsonContent, JsonOptions)
                    ?? new AiActorPlanResponse { Analysis = "Failed to parse response" };
            }

            return new AiActorPlanResponse { Analysis = content };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse LLM response as JSON");
            return new AiActorPlanResponse
            {
                Analysis = "Failed to parse response: " + ex.Message,
                Findings = new List<string> { "LLM response was not valid JSON" }
            };
        }
    }

    private async Task<AiActorAction> ExecuteActionAsync(
        Beacon.Core.Data.Entities.AiActor actor,
        AiActorActionPlan plan,
        Dictionary<string, int> newlyCreatedQueries,
        CancellationToken cancellationToken,
        int? executionId = null,
        int? planId = null)
    {
        var action = new AiActorAction
        {
            Reasoning = plan.Reasoning
        };

        try
        {
            switch (plan.ActionType.ToUpperInvariant())
            {
                case "CREATE_QUERY":
                    action.ActionType = AiActorActionType.CreateQuery;
                    await ExecuteCreateQueryAsync(actor, plan, action, cancellationToken);
                    break;

                case "CREATE_SUBSCRIPTION":
                    action.ActionType = AiActorActionType.CreateSubscription;
                    await ExecuteCreateSubscriptionAsync(actor, plan, action, newlyCreatedQueries, cancellationToken);
                    break;

                case "REFINE_QUERY":
                    action.ActionType = AiActorActionType.RefineQuery;
                    await ExecuteRefineQueryAsync(actor, plan, action, cancellationToken, executionId, planId);
                    break;

                case "ARCHIVE_QUERY":
                    action.ActionType = AiActorActionType.ArchiveQuery;
                    await ExecuteArchiveQueryAsync(plan, action, cancellationToken);
                    break;

                case "ARCHIVE_SUBSCRIPTION":
                    action.ActionType = AiActorActionType.ArchiveSubscription;
                    await ExecuteArchiveSubscriptionAsync(plan, action, cancellationToken);
                    break;

                default:
                    action.ErrorMessage = $"Unknown action type: {plan.ActionType}";
                    break;
            }
        }
        catch (Exception ex)
        {
            action.Success = false;
            action.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Failed to execute action {ActionType}", plan.ActionType);
        }

        return action;
    }

    private async Task ExecuteCreateQueryAsync(
        Beacon.Core.Data.Entities.AiActor actor,
        AiActorActionPlan plan,
        AiActorAction action,
        CancellationToken cancellationToken)
    {
        var name = plan.Parameters.GetValueOrDefault("name")?.ToString() ?? "Unnamed Query";
        var sql = plan.Parameters.GetValueOrDefault("sql")?.ToString();
        var description = plan.Parameters.GetValueOrDefault("description")?.ToString();

        if (string.IsNullOrWhiteSpace(sql))
        {
            action.ErrorMessage = "SQL is required for CREATE_QUERY";
            return;
        }

        action.QueryName = name;
        action.SqlQuery = sql;

        // Create query directly in context to get the ID back
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Single unit of work: build the Query + its first QueryStep via the navigation
        // collection so EF resolves the FK during SaveChanges. Previously this method
        // called SaveChanges twice and could leave an orphan Query row if the second
        // save (or the subsequent validation) blew up.
        var queryStep = new QueryStep
        {
            DataSourceId = actor.DataSourceId,
            StepOrder = 1,
            Name = "Step 1",
            SqlValue = sql,
            QueryId = 0
        };

        var query = new Query
        {
            Name = name,
            Description = description,
            AiActorId = actor.Id,
            Steps = { queryStep }
        };

        context.Queries.Add(query);
        await context.SaveChangesAsync(cancellationToken);

        // Validate the query by executing it - must run without exceptions
        try
        {
            var validationResult = await _queryService.ExecuteQueryAdvanced(query.Id, cancellationToken: cancellationToken);

            if (!validationResult.Success)
            {
                // Query execution failed - delete the query and mark action as failed
                context.QuerySteps.Remove(queryStep);
                context.Queries.Remove(query);
                await context.SaveChangesAsync(cancellationToken);

                action.Success = false;
                action.ErrorMessage = $"Query validation failed: {validationResult.ErrorMessage ?? "Unknown error"}";
                _logger.LogWarning("AI Actor {ActorId} created invalid query '{QueryName}': {Error}",
                    actor.Id, name, validationResult.ErrorMessage);
                return;
            }

            var rowCount = validationResult.StepResults.LastOrDefault()?.TotalRows ?? 0;
            _logger.LogInformation("AI Actor {ActorId} created valid query '{QueryName}' (ID: {QueryId}) returning {RowCount} rows",
                actor.Id, name, query.Id, rowCount);
        }
        catch (Exception ex)
        {
            // Query execution threw an exception - delete the query and mark action as failed
            context.QuerySteps.Remove(queryStep);
            context.Queries.Remove(query);
            await context.SaveChangesAsync(cancellationToken);

            action.Success = false;
            action.ErrorMessage = $"Query validation failed: {ex.Message}";
            _logger.LogWarning(ex, "AI Actor {ActorId} created invalid query '{QueryName}'", actor.Id, name);
            return;
        }

        action.Success = true;
        action.ResultEntityId = query.Id;
    }

    private async Task ExecuteCreateSubscriptionAsync(
        Beacon.Core.Data.Entities.AiActor actor,
        AiActorActionPlan plan,
        AiActorAction action,
        Dictionary<string, int> newlyCreatedQueries,
        CancellationToken cancellationToken)
    {
        int? queryId = null;

        // Try to get queryId from parameters
        if (plan.Parameters.TryGetValue("queryId", out var queryIdObj) && queryIdObj != null)
        {
            if (int.TryParse(queryIdObj.ToString(), out var id))
            {
                queryId = id;
            }
        }

        // Or try to look up by queryName for newly created queries
        if (!queryId.HasValue && plan.Parameters.TryGetValue("queryName", out var queryNameObj) && queryNameObj != null)
        {
            var queryName = queryNameObj.ToString();
            if (!string.IsNullOrEmpty(queryName) && newlyCreatedQueries.TryGetValue(queryName, out var createdId))
            {
                queryId = createdId;
            }
        }

        if (!queryId.HasValue)
        {
            action.ErrorMessage = "queryId or queryName is required for CREATE_SUBSCRIPTION";
            return;
        }

        var cronExpression = plan.Parameters.GetValueOrDefault("cronExpression")?.ToString() ?? "0 * * * *";
        var notificationTriggerStr = plan.Parameters.GetValueOrDefault("notificationTrigger")?.ToString() ?? "OnResultCountChange";

        action.SubscriptionQueryId = queryId;
        action.CronExpression = cronExpression;

        if (!Enum.TryParse<NotificationTrigger>(notificationTriggerStr, out var notificationTrigger))
        {
            notificationTrigger = NotificationTrigger.OnResultCountChange;
        }

        // Create subscription directly in context to get the ID back
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var subscription = new Subscription
        {
            QueryId = queryId.Value,
            CronExpression = cronExpression,
            NotificationTrigger = notificationTrigger,
            CreateTasks = true, // AI-created subscriptions create tasks by default
            ShowQuery = true,
            IncludeAttachment = false,
            AiActorId = actor.Id
        };

        context.Subscriptions.Add(subscription);
        await context.SaveChangesAsync(cancellationToken);

        action.Success = true;
        action.ResultEntityId = subscription.Id;
    }

    private async Task ExecuteRefineQueryAsync(
        Beacon.Core.Data.Entities.AiActor actor,
        AiActorActionPlan plan,
        AiActorAction action,
        CancellationToken cancellationToken,
        int? executionId = null,
        int? planId = null)
    {
        if (!plan.Parameters.TryGetValue("queryId", out var queryIdObj) || queryIdObj == null)
        {
            action.ErrorMessage = "queryId is required for REFINE_QUERY";
            return;
        }

        if (!int.TryParse(queryIdObj.ToString(), out var queryId))
        {
            action.ErrorMessage = "Invalid queryId";
            return;
        }

        var newSql = plan.Parameters.GetValueOrDefault("newSql")?.ToString();
        if (string.IsNullOrWhiteSpace(newSql))
        {
            action.ErrorMessage = "newSql is required for REFINE_QUERY";
            return;
        }

        action.TargetQueryId = queryId;
        action.SqlQuery = newSql;

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var query = await context.Queries
            .Include(q => q.Steps)
            .Where(q => q.Id == queryId && q.AiActorId == actor.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (query == null)
        {
            action.ErrorMessage = $"Query {queryId} not found or not owned by this actor";
            return;
        }

        // Check if query is locked
        if (query.IsLocked)
        {
            action.ErrorMessage = $"Query {queryId} is locked and cannot be modified by AI";
            _logger.LogWarning("AI Actor {ActorId} attempted to modify locked query {QueryId}",
                actor.Id, queryId);
            return;
        }

        var firstStep = query.Steps.OrderBy(s => s.StepOrder).FirstOrDefault();
        if (firstStep == null)
        {
            action.ErrorMessage = $"Query {queryId} has no steps to refine";
            return;
        }

        var previousSql = firstStep.SqlValue;
        var changeReason = plan.Parameters.GetValueOrDefault("reason")?.ToString();

        // Temporarily update the SQL to validate it
        firstStep.SqlValue = newSql;
        await context.SaveChangesAsync(cancellationToken);

        // Validate the refined query by executing it - must run without exceptions
        try
        {
            var validationResult = await _queryService.ExecuteQueryAdvanced(queryId, cancellationToken: cancellationToken);

            if (!validationResult.Success)
            {
                // Query execution failed - revert to original SQL
                firstStep.SqlValue = previousSql;
                await context.SaveChangesAsync(cancellationToken);

                action.Success = false;
                action.ErrorMessage = $"Refined query validation failed: {validationResult.ErrorMessage ?? "Unknown error"}";
                _logger.LogWarning("AI Actor {ActorId} refined query {QueryId} with invalid SQL: {Error}",
                    actor.Id, queryId, validationResult.ErrorMessage);
                return;
            }

            var rowCount = validationResult.StepResults.LastOrDefault()?.TotalRows ?? 0;
            _logger.LogInformation("AI Actor {ActorId} refined query {QueryId} - new SQL returns {RowCount} rows",
                actor.Id, queryId, rowCount);
        }
        catch (Exception ex)
        {
            // Query execution threw an exception - revert to original SQL
            firstStep.SqlValue = previousSql;
            await context.SaveChangesAsync(cancellationToken);

            action.Success = false;
            action.ErrorMessage = $"Refined query validation failed: {ex.Message}";
            _logger.LogWarning(ex, "AI Actor {ActorId} refined query {QueryId} with invalid SQL", actor.Id, queryId);
            return;
        }

        // Record the change in history
        var changeHistory = new QueryStepChangeHistory
        {
            QueryStepId = firstStep.Id,
            AiActorId = actor.Id,
            AiActorExecutionId = executionId,
            AiActorPlanId = planId,
            PreviousSql = previousSql,
            NewSql = newSql,
            ChangeReason = changeReason ?? plan.Reasoning,
            ChangeSource = ChangeSource.AiActor,
            ChangedAt = DateTime.UtcNow
        };
        context.QueryStepChangeHistory.Add(changeHistory);
        await context.SaveChangesAsync(cancellationToken);

        action.Success = true;
        action.ResultEntityId = queryId;
    }

    private async Task ExecuteArchiveQueryAsync(
        AiActorActionPlan plan,
        AiActorAction action,
        CancellationToken cancellationToken)
    {
        if (!plan.Parameters.TryGetValue("queryId", out var queryIdObj) || queryIdObj == null)
        {
            action.ErrorMessage = "queryId is required for ARCHIVE_QUERY";
            return;
        }

        if (!int.TryParse(queryIdObj.ToString(), out var queryId))
        {
            action.ErrorMessage = "Invalid queryId";
            return;
        }

        action.TargetQueryId = queryId;

        await _queryService.DeleteQuery(queryId, cancellationToken);

        action.Success = true;
        action.ResultEntityId = queryId;
    }

    private async Task ExecuteArchiveSubscriptionAsync(
        AiActorActionPlan plan,
        AiActorAction action,
        CancellationToken cancellationToken)
    {
        if (!plan.Parameters.TryGetValue("subscriptionId", out var subIdObj) || subIdObj == null)
        {
            action.ErrorMessage = "subscriptionId is required for ARCHIVE_SUBSCRIPTION";
            return;
        }

        if (!int.TryParse(subIdObj.ToString(), out var subscriptionId))
        {
            action.ErrorMessage = "Invalid subscriptionId";
            return;
        }

        action.TargetSubscriptionId = subscriptionId;

        await _subscriptionService.DeleteSubscription(subscriptionId, cancellationToken);

        action.Success = true;
        action.ResultEntityId = subscriptionId;
    }

    private async Task<int> GetNextConversationTurnAsync(int actorId, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var maxTurn = await context.AiActorConversations
            .Where(c => c.AiActorId == actorId)
            .MaxAsync(c => (int?)c.TurnNumber, cancellationToken);

        return (maxTurn ?? 0) + 1;
    }

    #endregion

    #region Plan Approval Workflow

    public async Task<AiActorPlanResult> GeneratePlanAsync(
        GeneratePlanOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var actor = await context.AiActors
            .Include(a => a.DataSource)
            .Where(a => a.Id == options.ActorId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconException($"AI Actor with ID {options.ActorId} not found");

        try
        {
            var schemaContext = await GetSchemaContextAsync(actor.DataSourceId, cancellationToken);
            var existingQueries = await GetQueryContextAsync(actor.Id, cancellationToken);

            // Build the prompt based on context
            string userPrompt;
            string systemPrompt;

            if (!string.IsNullOrWhiteSpace(options.UserInstruction))
            {
                userPrompt = AiActorPrompts.BuildRefinementPrompt(
                    actor, schemaContext, existingQueries, options.UserInstruction);
                systemPrompt = AiActorPrompts.RefinementSystemPrompt;
            }
            else if (options.TriggeringSubscriptionId.HasValue)
            {
                var recentResults = await GetRecentResultsAsync(options.TriggeringSubscriptionId.Value, cancellationToken);
                userPrompt = AiActorPrompts.BuildThinkCyclePrompt(
                    actor, schemaContext, existingQueries, options.TriggeringSubscriptionId, recentResults);
                systemPrompt = AiActorPrompts.ThinkCycleSystemPrompt;
            }
            else
            {
                userPrompt = AiActorPrompts.BuildInitialSetupPrompt(actor, schemaContext);
                systemPrompt = AiActorPrompts.InitialSetupSystemPrompt;
            }

            // Add locked queries info to context. Resolve locked ids in a single DB
            // round-trip — the previous shape issued one query per existingQueries entry.
            var candidateQueryIds = existingQueries
                .Select(x => x.QueryId)
                .ToList();

            var lockedQueryIdSet = await context.Queries
                .AsNoTracking()
                .Where(x => candidateQueryIds.Contains(x.Id))
                .Where(x => x.IsLocked)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);

            var lockedQueryIds = existingQueries
                .Where(x => lockedQueryIdSet.Contains(x.QueryId))
                .Select(x => x.QueryId)
                .ToList();

            if (lockedQueryIds.Count > 0)
            {
                userPrompt += $"\n\n## Locked Queries (Cannot Be Modified)\nThe following query IDs are locked and should NOT be refined: {string.Join(", ", lockedQueryIds)}";
            }

            var llmRequest = new LlmRequest
            {
                SystemPrompt = systemPrompt,
                Messages = new List<ChatMessage>
                {
                    new(ConversationRole.User, userPrompt)
                },
                Temperature = 0.3m,
                MaxTokens = 4096
            };

            var llmResponse = await _llmProvider.CompleteAsync(llmRequest, cancellationToken);
            var planResponse = ParseLlmResponse(llmResponse.Content);

            // Convert actions to ProposedAction list with additional context
            var proposedActions = new List<ProposedAction>();
            foreach (var actionPlan in planResponse.Actions)
            {
                var proposedAction = new ProposedAction
                {
                    ActionType = Enum.TryParse<AiActorActionType>(actionPlan.ActionType, true, out var at)
                        ? at : AiActorActionType.CreateQuery,
                    Reasoning = actionPlan.Reasoning,
                    Parameters = actionPlan.Parameters
                };

                // For refine actions, add current SQL and locked status
                if (actionPlan.ActionType.Equals("REFINE_QUERY", StringComparison.OrdinalIgnoreCase) &&
                    actionPlan.Parameters.TryGetValue("queryId", out var queryIdObj) &&
                    int.TryParse(queryIdObj?.ToString(), out var queryId))
                {
                    var targetQuery = existingQueries.FirstOrDefault(q => q.QueryId == queryId);
                    proposedAction = proposedAction with
                    {
                        TargetQueryId = queryId,
                        TargetQueryName = targetQuery?.QueryName,
                        CurrentSql = targetQuery?.Sql,
                        ProposedSql = actionPlan.Parameters.GetValueOrDefault("newSql")?.ToString(),
                        IsLocked = await context.Queries.AnyAsync(q => q.Id == queryId && q.IsLocked, cancellationToken)
                    };
                }

                proposedActions.Add(proposedAction);
            }

            // Create the plan record
            var plan = new AiActorPlan
            {
                AiActorId = actor.Id,
                Status = AiActorPlanStatus.PendingApproval,
                UserInstruction = options.UserInstruction,
                Analysis = planResponse.Analysis,
                FindingsJson = planResponse.Findings?.Count > 0
                    ? JsonSerializer.Serialize(planResponse.Findings, JsonOptions)
                    : null,
                ActionsJson = JsonSerializer.Serialize(proposedActions, JsonOptions),
                ProposedAt = DateTime.UtcNow,
                TokensUsed = llmResponse.TotalTokens,
                EstimatedCost = llmResponse.Cost,
                Model = llmResponse.Model,
                Version = options.ParentPlanId.HasValue ? await GetNextPlanVersionAsync(options.ParentPlanId.Value, cancellationToken) : 1,
                ParentPlanId = options.ParentPlanId
            };

            context.AiActorPlans.Add(plan);
            await context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Generated plan {PlanId} for actor {ActorId} with {ActionCount} proposed actions",
                plan.Id, actor.Id, proposedActions.Count);

            return AiActorPlanResult.CreateSuccess(
                plan.Id,
                planResponse.Analysis,
                planResponse.Findings,
                proposedActions,
                llmResponse.TotalTokens,
                llmResponse.Cost);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate plan for actor {ActorId}", options.ActorId);
            return AiActorPlanResult.CreateFailure(ex.Message);
        }
    }

    public async Task<AiActorThinkResult> ApprovePlanAsync(
        ApprovePlanOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var plan = await context.AiActorPlans
            .Include(p => p.AiActor)
                .ThenInclude(a => a.DataSource)
            .Where(p => p.Id == options.PlanId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconException($"Plan with ID {options.PlanId} not found");

        if (plan.Status != AiActorPlanStatus.PendingApproval)
        {
            throw new BeaconException($"Plan is not pending approval (status: {plan.Status})");
        }

        var actor = plan.AiActor;

        // Mark plan as executing
        plan.Status = AiActorPlanStatus.Executing;
        plan.ReviewedAt = DateTime.UtcNow;
        plan.ReviewedByUserId = options.UserId;
        plan.ReviewerComment = options.Comment;
        await context.SaveChangesAsync(cancellationToken);

        // Create execution record
        var execution = new AiActorExecution
        {
            AiActorId = actor.Id,
            AiActorPlanId = plan.Id,
            Phase = AiActorExecutionPhase.Executing,
            StartedAt = DateTime.UtcNow,
            DecisionSummary = plan.Analysis,
            DetailedAnalysis = plan.Analysis,
            FindingsJson = plan.FindingsJson,
            TokensUsed = plan.TokensUsed,
            EstimatedCost = plan.EstimatedCost,
            Model = plan.Model
        };
        context.AiActorExecutions.Add(execution);
        await context.SaveChangesAsync(cancellationToken);

        try
        {
            // Parse and execute actions
            var proposedActions = JsonSerializer.Deserialize<List<ProposedAction>>(plan.ActionsJson, JsonOptions)
                ?? new List<ProposedAction>();

            var executedActions = new List<AiActorAction>();
            var newlyCreatedQueries = new Dictionary<string, int>();

            foreach (var proposedAction in proposedActions)
            {
                // Skip locked queries for RefineQuery actions
                if (proposedAction.ActionType == AiActorActionType.RefineQuery && proposedAction.IsLocked)
                {
                    executedActions.Add(new AiActorAction
                    {
                        ActionType = AiActorActionType.RefineQuery,
                        Success = false,
                        Reasoning = proposedAction.Reasoning,
                        ErrorMessage = $"Query {proposedAction.TargetQueryId} is locked"
                    });
                    continue;
                }

                var actionPlan = new AiActorActionPlan
                {
                    ActionType = proposedAction.ActionType.ToString().ToUpperInvariant().Replace("QUERY", "_QUERY").Replace("SUBSCRIPTION", "_SUBSCRIPTION"),
                    Reasoning = proposedAction.Reasoning,
                    Parameters = proposedAction.Parameters
                };

                // Fix action type format
                actionPlan.ActionType = proposedAction.ActionType switch
                {
                    AiActorActionType.CreateQuery => "CREATE_QUERY",
                    AiActorActionType.RefineQuery => "REFINE_QUERY",
                    AiActorActionType.ArchiveQuery => "ARCHIVE_QUERY",
                    AiActorActionType.CreateSubscription => "CREATE_SUBSCRIPTION",
                    AiActorActionType.ArchiveSubscription => "ARCHIVE_SUBSCRIPTION",
                    _ => proposedAction.ActionType.ToString()
                };

                var action = await ExecuteActionAsync(actor, actionPlan, newlyCreatedQueries, cancellationToken, execution.Id, plan.Id);
                executedActions.Add(action);

                if (action.ActionType == AiActorActionType.CreateQuery &&
                    action.Success && action.ResultEntityId.HasValue &&
                    !string.IsNullOrEmpty(action.QueryName))
                {
                    newlyCreatedQueries[action.QueryName] = action.ResultEntityId.Value;
                }

                switch (action.ActionType)
                {
                    case AiActorActionType.CreateQuery when action.Success:
                        execution.QueriesCreated++;
                        break;
                    case AiActorActionType.RefineQuery when action.Success:
                        execution.QueriesRefined++;
                        break;
                    case AiActorActionType.CreateSubscription when action.Success:
                        execution.SubscriptionsCreated++;
                        break;
                }
            }

            execution.ActionsJson = JsonSerializer.Serialize(executedActions, JsonOptions);
            execution.Phase = AiActorExecutionPhase.Completed;
            execution.CompletedAt = DateTime.UtcNow;

            plan.Status = AiActorPlanStatus.Executed;
            plan.ExecutedAt = DateTime.UtcNow;
            plan.AiActorExecutionId = execution.Id;

            actor.TotalTokensUsed += execution.TokensUsed;
            actor.TotalCost += execution.EstimatedCost;

            await context.SaveChangesAsync(cancellationToken);

            var result = AiActorThinkResult.CreateSuccess(execution.Id, execution.DecisionSummary);
            result.Actions = executedActions;
            result.QueriesCreated = execution.QueriesCreated;
            result.QueriesRefined = execution.QueriesRefined;
            result.SubscriptionsCreated = execution.SubscriptionsCreated;
            result.TokensUsed = execution.TokensUsed;
            result.EstimatedCost = execution.EstimatedCost;
            result.Duration = execution.CompletedAt.Value - execution.StartedAt;

            _logger.LogInformation("Executed approved plan {PlanId} for actor {ActorId}", plan.Id, actor.Id);

            return result;
        }
        catch (Exception ex)
        {
            execution.Phase = AiActorExecutionPhase.Failed;
            execution.ErrorMessage = ex.Message;
            execution.CompletedAt = DateTime.UtcNow;

            plan.Status = AiActorPlanStatus.Rejected;

            await context.SaveChangesAsync(cancellationToken);

            _logger.LogError(ex, "Failed to execute approved plan {PlanId}", options.PlanId);
            return AiActorThinkResult.CreateFailure(execution.Id, ex.Message);
        }
    }

    public async Task RejectPlanAsync(
        RejectPlanOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var plan = await context.AiActorPlans
            .Where(p => p.Id == options.PlanId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconException($"Plan with ID {options.PlanId} not found");

        if (plan.Status != AiActorPlanStatus.PendingApproval)
        {
            throw new BeaconException($"Plan is not pending approval (status: {plan.Status})");
        }

        plan.Status = AiActorPlanStatus.Rejected;
        plan.ReviewedAt = DateTime.UtcNow;
        plan.ReviewedByUserId = options.UserId;
        plan.ReviewerComment = options.Reason;

        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Rejected plan {PlanId}: {Reason}", options.PlanId, options.Reason);
    }

    public async Task<AiActorPlanResult> RequestPlanRevisionAsync(
        RequestRevisionOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var plan = await context.AiActorPlans
            .Where(p => p.Id == options.PlanId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconException($"Plan with ID {options.PlanId} not found");

        if (plan.Status != AiActorPlanStatus.PendingApproval)
        {
            throw new BeaconException($"Plan is not pending approval (status: {plan.Status})");
        }

        // Mark original plan as revision requested
        plan.Status = AiActorPlanStatus.RevisionRequested;
        plan.ReviewedAt = DateTime.UtcNow;
        plan.ReviewedByUserId = options.UserId;
        plan.ReviewerComment = options.Feedback;
        await context.SaveChangesAsync(cancellationToken);

        // Generate new plan with the feedback
        var newPlanOptions = new GeneratePlanOptions
        {
            ActorId = plan.AiActorId,
            UserInstruction = $"{plan.UserInstruction}\n\nRevision feedback: {options.Feedback}",
            ParentPlanId = plan.Id,
            PreviousFeedback = options.Feedback
        };

        return await GeneratePlanAsync(newPlanOptions, cancellationToken);
    }

    public async Task<List<PendingPlanSummary>> GetPendingPlansAsync(
        int actorId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.AiActorPlans
            .Where(p => p.AiActorId == actorId && p.Status == AiActorPlanStatus.PendingApproval)
            .OrderByDescending(p => p.ProposedAt)
            .Select(p => new PendingPlanSummary
            {
                PlanId = p.Id,
                ActorId = p.AiActorId,
                ActorName = p.AiActor.Name,
                UserInstruction = p.UserInstruction,
                Analysis = p.Analysis,
                ActionCount = CountJsonArrayElements(p.ActionsJson),
                ProposedAt = p.ProposedAt,
                Version = p.Version,
                TokensUsed = p.TokensUsed,
                EstimatedCost = p.EstimatedCost
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<AiActorPlan?> GetPlanAsync(
        int planId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.AiActorPlans
            .Include(p => p.AiActor)
            .Include(p => p.ParentPlan)
            .Include(p => p.AiActorExecution)
            .Where(p => p.Id == planId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<int> GetNextPlanVersionAsync(int parentPlanId, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var maxVersion = await context.AiActorPlans
            .Where(p => p.Id == parentPlanId || p.ParentPlanId == parentPlanId)
            .MaxAsync(p => (int?)p.Version, cancellationToken);

        return (maxVersion ?? 0) + 1;
    }

    private static int CountJsonArrayElements(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
        }
        catch
        {
            return 0;
        }
    }

    #endregion
}

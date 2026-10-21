using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core;
using Beacon.Core.Authorization;
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
using Beacon.Core.Helpers;
using Beacon.Core.SavedQueries;
using Beacon.Core.Services.Security;
using Beacon.Core.SecurityAudit;

namespace Beacon.AI.Services.Ai.AiActor;

/// <summary>
/// Service for managing AI Actors that autonomously monitor data sources. A think cycle runs only while its actor may
/// still work ad hoc with the actor's data source: a background cycle runs as the actor's creator
/// (<see cref="IRunAsScope"/>) and is skipped, recorded on its execution, when the creator may not; a cycle a request
/// asks for runs as the request's caller.
/// </summary>
public class AiActorService : IAiActorServiceExtended
{
    /// <summary>Recorded on a background think cycle skipped because the actor's creator may no longer run it.</summary>
    public const string OwnerNotAuthorizedMessage = "Skipped: owner not authorized.";

    /// <summary>Why a think cycle did not start: the actor thought within the configured cooldown.</summary>
    public const string ThinkCooldownMessage = "This AI actor thought recently; think cycles run at most once per cooldown.";

    /// <summary>Why a think cycle did not start: the actor was archived or removed meanwhile.</summary>
    public const string ActorUnavailableMessage = "This AI actor is no longer available.";

    /// <summary>The change source a refinement records on its query version.</summary>
    public const string AiActorChangeSource = "AiActor";

    /// <summary>Why a decision on a plan that is no longer pending approval is refused.</summary>
    public const string PlanNotPendingMessage = "This plan is no longer pending approval: it was decided already.";

    /// <summary>Why, with the approval workflow on, an AI actor does not subscribe to a query whose SQL nobody approved.</summary>
    public const string NotApprovedForSubscriptionMessage =
        "it has no approved active version yet, so a schedule would run SQL nobody approved; subscribe to it once its SQL is approved";

    // The longest change reason a query version stores.
    private const int MaxChangeReasonLength = 2000;

    private readonly IDbContextFactory<BeaconContext> _contextFactory;
    private readonly ILlmProvider _llmProvider;
    private readonly IDatabaseMetadataService _metadataService;
    private readonly IQueryService _queryService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IBeaconScheduler _beaconScheduler;
    private readonly IRunAsScope _runAs;
    private readonly IExecutionAccessCheck _executionAccess;
    private readonly IStepSqlSaveCheck _saveCheck;
    private readonly IQueryProposalCheck _proposalCheck;
    private readonly IQueryVersionService _versionService;
    private readonly BeaconConfiguration _configuration;
    private readonly ISecurityAuditRecorder _securityAudit;
    private readonly ILogger<AiActorService> _logger;
    private readonly IOptionsMonitor<AiActorOptions>? _options;
    private readonly TimeProvider _timeProvider;
    private readonly IAiActorThinkClaim _thinkClaim;

    private static readonly TimeSpan FailureSaveTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Obsolete("Use the constructor that takes the think cooldown options, time provider and think claim.")]
    public AiActorService(
        IDbContextFactory<BeaconContext> contextFactory,
        ILlmProvider llmProvider,
        IDatabaseMetadataService metadataService,
        IQueryService queryService,
        ISubscriptionService subscriptionService,
        IBeaconScheduler beaconScheduler,
        IRunAsScope runAs,
        IExecutionAccessCheck executionAccess,
        IStepSqlSaveCheck saveCheck,
        IQueryProposalCheck proposalCheck,
        IQueryVersionService versionService,
        BeaconConfiguration configuration,
        ISecurityAuditRecorder securityAudit,
        ILogger<AiActorService> logger)
        : this(
            contextFactory, llmProvider, metadataService, queryService, subscriptionService, beaconScheduler, runAs,
            executionAccess, saveCheck, proposalCheck, versionService, configuration, securityAudit, logger,
            null, null, null)
    {
    }

    public AiActorService(
        IDbContextFactory<BeaconContext> contextFactory,
        ILlmProvider llmProvider,
        IDatabaseMetadataService metadataService,
        IQueryService queryService,
        ISubscriptionService subscriptionService,
        IBeaconScheduler beaconScheduler,
        IRunAsScope runAs,
        IExecutionAccessCheck executionAccess,
        IStepSqlSaveCheck saveCheck,
        IQueryProposalCheck proposalCheck,
        IQueryVersionService versionService,
        BeaconConfiguration configuration,
        ISecurityAuditRecorder securityAudit,
        ILogger<AiActorService> logger,
        IOptionsMonitor<AiActorOptions>? options,
        TimeProvider? timeProvider,
        IAiActorThinkClaim? thinkClaim)
    {
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _thinkClaim = thinkClaim ?? new EfAiActorThinkClaim(contextFactory);
        _contextFactory = contextFactory;
        _llmProvider = llmProvider;
        _metadataService = metadataService;
        _queryService = queryService;
        _subscriptionService = subscriptionService;
        _beaconScheduler = beaconScheduler;
        _runAs = runAs;
        _executionAccess = executionAccess;
        _saveCheck = saveCheck;
        _proposalCheck = proposalCheck;
        _versionService = versionService;
        _configuration = configuration;
        _securityAudit = securityAudit;
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
                _logger.LogError("Failed to run initial setup for actor {ActorId} ({Failure})", actor.Id, FailureCodes.Describe(ex));
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
                    ActorStatus = (AiActorStatus?)x.AiActor!.Status,
                    x.AiActor!.LastThinkTime
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

        if (CooldownRemaining(subscriptionInfo.LastThinkTime) is not null)
        {
            _logger.LogDebug("Skipping think cycle for actor {ActorId} - within the think cooldown", actorId);
            return;
        }

        // Enqueue the think cycle as a background job so the subscription-execution
        // pipeline does not block on LLM round-trips. The job runs under its own scope
        // and its own CancellationToken.
        var jobId = await _beaconScheduler.EnqueueAiActorThinkCycle(actorId, subscriptionId);

        _logger.LogInformation(
            "Subscription {SubscriptionId} executed, enqueued think cycle job {JobId} for actor {ActorId}",
            subscriptionId, jobId, actorId);
    }

    public Task<AiActorThinkResult> ExecuteThinkCycleAsync(
        int actorId,
        int? triggeringSubscriptionId = null,
        CancellationToken cancellationToken = default)
    {
        // A cycle a caller asks for does not bypass the cooldown: it is answered with the time left.
        return ExecuteThinkCycleCoreAsync(actorId, triggeringSubscriptionId, false, cancellationToken);
    }

    private async Task<AiActorThinkResult> ExecuteThinkCycleCoreAsync(
        int actorId,
        int? triggeringSubscriptionId,
        bool background,
        CancellationToken cancellationToken)
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

        // Before any LLM call or SQL: the cycle's actor (a background cycle's creator, or a request's caller) may still
        // work ad hoc with the actor's data source, which every action of the cycle reads.
        if (!await _executionAccess.AllowsAsync(ProjectAction.Query, [actor.DataSourceId], "AI actor", actorId, cancellationToken))
        {
            if (!_runAs.IsSet)
            {
                throw new BeaconForbiddenException("Working with this AI actor's data source is not allowed.");
            }

            return await RecordSkippedCycleAsync(context, actorId, triggeringSubscriptionId, startTime, cancellationToken);
        }

        // At most one cycle per actor per cooldown. The in-memory check answers the common case; the conditional claim
        // makes sure two hosts (or two job runs) cannot both start one.
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var remaining = CooldownRemaining(actor.LastThinkTime);
        var claim = remaining is not null
            ? ThinkClaimResult.Held
            : Cooldown <= TimeSpan.Zero
                ? ThinkClaimResult.Claimed
                : await _thinkClaim.TryClaimAsync(actorId, now, now - Cooldown, cancellationToken);
        if (claim != ThinkClaimResult.Claimed)
        {
            if (background)
            {
                _logger.LogDebug("Skipping think cycle for actor {ActorId} - within the think cooldown", actorId);
            }

            return AiActorThinkResult.CreateFailure(
                0,
                claim == ThinkClaimResult.Missing ? ActorUnavailableMessage : CooldownMessage(remaining));
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
                var action = await ExecuteOrProposeAsync(
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
            // LastThinkTime is written only by the think claim, so a late completion cannot overwrite a newer claim; the
            // completion time is on the execution record.
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RecordCancelledAsync(context, execution);
            throw;
        }
        catch (Exception ex)
        {
            execution.Phase = AiActorExecutionPhase.Failed;
            execution.ErrorMessage = ex.Message;
            execution.CompletedAt = DateTime.UtcNow;

            actor.Status = AiActorStatus.Failed;
            actor.LastError = ex.Message;

            await TrySaveFailureAsync(context);

            _logger.LogError("Think cycle failed for actor {ActorId} ({Failure})", actorId, FailureCodes.Describe(ex));

            return AiActorThinkResult.CreateFailure(execution.Id, ex.Message);
        }
    }

    public Task ExecuteThinkCycleBackgroundAsync(int actorId, int? triggeringSubscriptionId = null)
    {
        return ExecuteThinkCycleBackgroundAsync(actorId, triggeringSubscriptionId, CancellationToken.None);
    }

    public async Task ExecuteThinkCycleBackgroundAsync(
        int actorId,
        int? triggeringSubscriptionId,
        CancellationToken cancellationToken)
    {
        // A background cycle acts as the actor's creator as they are now, set before anything resolves the scope's caller.
        await RunAsCreatorAsync(actorId, cancellationToken);

        await ExecuteThinkCycleCoreAsync(actorId, triggeringSubscriptionId, true, cancellationToken);
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
                var action = await ExecuteOrProposeAsync(actor, actionPlan, newlyCreatedQueries, cancellationToken);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RecordCancelledAsync(context, execution);
            throw;
        }
        catch (Exception ex)
        {
            execution.Phase = AiActorExecutionPhase.Failed;
            execution.ErrorMessage = ex.Message;
            execution.CompletedAt = DateTime.UtcNow;
            await TrySaveFailureAsync(context);

            _logger.LogError("Refinement failed for actor {ActorId} ({Failure})", actorId, FailureCodes.Describe(ex));
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

    // The failure record is written even when the cycle was cancelled (the cancelled token would drop it), bounded so a
    // database that does not answer cannot hold the cycle. A save that fails is logged by class and never replaces the
    // failure being handled.
    private async Task TrySaveFailureAsync(BeaconContext context)
    {
        try
        {
            using var timeout = new CancellationTokenSource(FailureSaveTimeout);
            await context.SaveChangesAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError("The failure record could not be saved ({Failure})", FailureCodes.Describe(ex));
        }
    }

    // A cancelled cycle (host shutdown) only closes its own execution row; the actor and the plan keep their status, and
    // whatever the interrupted cycle had changed but not saved (a plan marked executed, new queries) is not written.
    private async Task RecordCancelledAsync(BeaconContext context, AiActorExecution execution)
    {
        var unsaved = context.ChangeTracker.Entries()
            .Where(x => x.Entity != execution)
            .ToList();
        foreach (var entry in unsaved)
        {
            entry.State = EntityState.Detached;
        }

        execution.Phase = AiActorExecutionPhase.Failed;
        execution.ErrorMessage = "cancelled";
        execution.CompletedAt = DateTime.UtcNow;

        await TrySaveFailureAsync(context);
    }

    private TimeSpan Cooldown =>
        TimeSpan.FromMinutes((_options?.CurrentValue ?? new AiActorOptions()).ThinkCooldownMinutes);

    // Null when a cycle may start now; otherwise the time left of the cooldown since the actor last thought.
    private TimeSpan? CooldownRemaining(DateTime? lastThinkTime)
    {
        if (lastThinkTime == null || Cooldown <= TimeSpan.Zero)
        {
            return null;
        }

        var remaining = lastThinkTime.Value + Cooldown - _timeProvider.GetUtcNow().UtcDateTime;

        return remaining > TimeSpan.Zero ? remaining : null;
    }

    private static string CooldownMessage(TimeSpan? remaining)
    {
        return remaining is { } left
            ? $"{ThinkCooldownMessage} Try again in {Math.Ceiling(left.TotalMinutes)} minute(s)."
            : $"{ThinkCooldownMessage} Another cycle has just started; try again shortly.";
    }

    private async Task RunAsCreatorAsync(int actorId, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var actor = await context.AiActors
            .Where(x => x.Id == actorId)
            .Select(x =>
                new
                {
                    x.CreatedByUserId
                })
            .FirstOrDefaultAsync(cancellationToken);

        // A missing actor fails its cycle as before; there is no creator to run as.
        if (actor != null)
        {
            _runAs.RunAs(actor.CreatedByUserId);
        }
    }

    // A background cycle its creator may no longer run: recorded as a skipped execution (no LLM call, no SQL, no change
    // to the actor), not as a failure.
    private static async Task<AiActorThinkResult> RecordSkippedCycleAsync(
        BeaconContext context,
        int actorId,
        int? triggeringSubscriptionId,
        DateTime startTime,
        CancellationToken cancellationToken)
    {
        var execution = new AiActorExecution
        {
            AiActorId = actorId,
            TriggeringSubscriptionId = triggeringSubscriptionId,
            Phase = AiActorExecutionPhase.Skipped,
            StartedAt = startTime,
            CompletedAt = DateTime.UtcNow,
            ErrorMessage = OwnerNotAuthorizedMessage
        };
        context.AiActorExecutions.Add(execution);
        await context.SaveChangesAsync(cancellationToken);

        var result = AiActorThinkResult.CreateFailure(execution.Id, OwnerNotAuthorizedMessage);
        result.Phase = AiActorExecutionPhase.Skipped;

        return result;
    }

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
                var action = await ExecuteOrProposeAsync(actor, actionPlan, newlyCreatedQueries, cancellationToken);
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

            // The initial setup does not count towards the think cooldown: LastThinkTime stays as it is.
            actor.ThinkCount++;
            actor.TotalTokensUsed += execution.TokensUsed;
            actor.TotalCost += execution.EstimatedCost;

            await context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Initial setup completed for actor {ActorId}: {QueriesCreated} queries, {SubscriptionsCreated} subscriptions",
                actorId, execution.QueriesCreated, execution.SubscriptionsCreated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RecordCancelledAsync(context, execution);
            throw;
        }
        catch (Exception ex)
        {
            execution.Phase = AiActorExecutionPhase.Failed;
            execution.ErrorMessage = ex.Message;
            execution.CompletedAt = DateTime.UtcNow;
            await TrySaveFailureAsync(context);
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
            _logger.LogWarning("Failed to fetch schema metadata for data source {DataSourceId} ({Failure})", dataSourceId, FailureCodes.Describe(ex));
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
            _logger.LogWarning("Failed to parse LLM response as JSON ({Failure})", FailureCodes.Describe(ex));
            return new AiActorPlanResponse
            {
                Analysis = "Failed to parse response: " + ex.Message,
                Findings = new List<string> { "LLM response was not valid JSON" }
            };
        }
    }

    // Used by the think / refine / initial-setup loops. Actors that require approval only record
    // what they would do; the approve path calls ExecuteActionAsync directly.
    internal Task<AiActorAction> ExecuteOrProposeAsync(
        Beacon.Core.Data.Entities.AiActor actor,
        AiActorActionPlan plan,
        Dictionary<string, int> newlyCreatedQueries,
        CancellationToken cancellationToken)
    {
        if (!AiActorActionGuard.ShouldExecute(actor))
        {
            return Task.FromResult(ProposeAction(actor, plan));
        }

        return ExecuteActionAsync(actor, plan, newlyCreatedQueries, cancellationToken);
    }

    // A malformed plan is a failed action, as it is on the execute path — never a failed cycle.
    private AiActorAction ProposeAction(Beacon.Core.Data.Entities.AiActor actor, AiActorActionPlan plan)
    {
        try
        {
            return AiActorActionGuard.ToProposed(plan);
        }
        catch (Exception ex)
        {
            _logger.LogError("AI Actor {ActorId} could not record a proposed action ({ExceptionType})", actor.Id, ex.GetType().Name);

            return new AiActorAction
            {
                Reasoning = plan?.Reasoning,
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <param name="contributors">For the actions of an approved plan: the plan's reviewer and whoever asked for its
    /// revisions, recorded on the query changes the actions submit (none of them approves those).</param>
    private async Task<AiActorAction> ExecuteActionAsync(
        Beacon.Core.Data.Entities.AiActor actor,
        AiActorActionPlan plan,
        Dictionary<string, int> newlyCreatedQueries,
        CancellationToken cancellationToken,
        int? executionId = null,
        int? planId = null,
        IReadOnlyList<string>? contributors = null)
    {
        var action = new AiActorAction
        {
            Reasoning = plan?.Reasoning
        };

        try
        {
            switch (plan.ActionType.ToUpperInvariant())
            {
                case "CREATE_QUERY":
                    action.ActionType = AiActorActionType.CreateQuery;
                    await ExecuteCreateQueryAsync(actor, plan, action, contributors ?? [], cancellationToken);
                    break;

                case "CREATE_SUBSCRIPTION":
                    action.ActionType = AiActorActionType.CreateSubscription;
                    await ExecuteCreateSubscriptionAsync(actor, plan, action, newlyCreatedQueries, cancellationToken);
                    break;

                case "REFINE_QUERY":
                    action.ActionType = AiActorActionType.RefineQuery;
                    await ExecuteRefineQueryAsync(actor, plan, action, contributors ?? [], cancellationToken, executionId, planId);
                    break;

                case "ARCHIVE_QUERY":
                    action.ActionType = AiActorActionType.ArchiveQuery;
                    await ExecuteArchiveQueryAsync(actor, plan, action, cancellationToken);
                    break;

                case "ARCHIVE_SUBSCRIPTION":
                    action.ActionType = AiActorActionType.ArchiveSubscription;
                    await ExecuteArchiveSubscriptionAsync(actor, plan, action, cancellationToken);
                    break;

                default:
                    action.ErrorMessage = $"Unknown action type: {plan.ActionType}";
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            action.Success = false;
            action.ErrorMessage = ex.Message;
            _logger.LogError("Failed to execute action {ActionType} ({Failure})", plan?.ActionType, FailureCodes.Describe(ex));
        }

        return action;
    }

    private async Task ExecuteCreateQueryAsync(
        Beacon.Core.Data.Entities.AiActor actor,
        AiActorActionPlan plan,
        AiActorAction action,
        IReadOnlyList<string> contributors,
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

        // The model's SQL passes the same check as a query saved from the editor before anything is stored: the execution
        // gate in the source's dialect and, where Beacon holds the source's metadata, its own tables and columns only.
        var rejection = await _saveCheck.RejectionAsync(actor.DataSourceId, sql, [], cancellationToken);
        if (rejection != null)
        {
            action.ErrorMessage = $"Query validation failed: {rejection}";
            _logger.LogWarning("AI Actor {ActorId} proposed a query the save check refused", actor.Id);
            return;
        }

        // Create query directly in context to get the ID back
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var ownedQueries = await context.Queries
            .Where(x => x.AiActorId == actor.Id)
            .CountAsync(cancellationToken);

        var capError = AiActorActionGuard.CheckQueryCap(ownedQueries, actor.MaxQueries);
        if (capError != null)
        {
            action.ErrorMessage = capError;
            _logger.LogWarning("AI Actor {ActorId} hit the query cap", actor.Id);
            return;
        }

        // Validated before anything is stored — with the approval workflow on, without running it (DraftFailureAsync): a
        // query whose SQL does not pass is never written (nor deleted again).
        var failure = await DraftFailureAsync(
            0,
            [
                new QueryStepData
                {
                    StepOrder = 1,
                    Name = "Step 1",
                    SqlValue = sql,
                    DataSourceId = actor.DataSourceId
                }
            ],
            null,
            cancellationToken);
        if (failure != null)
        {
            action.Success = false;
            action.ErrorMessage = $"Query validation failed: {failure}";
            _logger.LogWarning("AI Actor {ActorId} created invalid query '{QueryName}' ({ErrorClass})",
                actor.Id, name, DatabaseErrorRedactor.ClassOnly(failure, _logger));
            return;
        }

        // With the approval workflow on, the new query's SQL waits for approval like a person's: the query is created with
        // nothing live and its SQL submitted as its first version, with the actor's creator as its requester and the
        // plan's reviewer and revision requesters recorded, so none of them approves it and nothing runs until approved.
        if (_configuration.ApprovalWorkflow.Enabled)
        {
            var change = await _versionService.SubmitNewQueryAsync(
                new Query
                {
                    Name = name,
                    Description = description,
                    AiActorId = actor.Id,
                    OwnerUserId = actor.CreatedByUserId,
                    CreatedByUserId = actor.CreatedByUserId
                },
                new QueryData
                {
                    Name = name,
                    Description = description,
                    Steps =
                    [
                        new QueryStepData
                        {
                            StepOrder = 1,
                            Name = "Step 1",
                            SqlValue = sql,
                            DataSourceId = actor.DataSourceId
                        }
                    ]
                },
                new QueryChangeSubmission(actor.CreatedByUserId, null, AiActorChangeSource, Truncated(plan.Reasoning, MaxChangeReasonLength), "AI actor's new query")
                {
                    ContributorUserIds = contributors
                },
                cancellationToken);

            _logger.LogInformation("AI Actor {ActorId} created query {QueryId}; its SQL waits for approval", actor.Id, change.Version.QueryId);

            action.Success = true;
            action.ResultEntityId = change.Version.QueryId;
            return;
        }

        // Single unit of work: build the Query + its first QueryStep via the navigation collection so EF resolves the FK
        // during SaveChanges.
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
            OwnerUserId = actor.CreatedByUserId,
            CreatedByUserId = actor.CreatedByUserId,
            Steps = { queryStep }
        };

        context.Queries.Add(query);
        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("AI Actor {ActorId} created valid query '{QueryName}' (ID: {QueryId})", actor.Id, name, query.Id);

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

        var targetQuery = await context.Queries
            .Where(x => x.Id == queryId.Value)
            .Select(x =>
                new
                {
                    x.AiActorId
                })
            .FirstOrDefaultAsync(cancellationToken);

        if (targetQuery == null)
        {
            action.ErrorMessage = $"Query {queryId} not found";
            return;
        }

        var ownershipError = AiActorActionGuard.CheckOwnership(actor.Id, targetQuery.AiActorId, false);
        if (ownershipError != null)
        {
            action.ErrorMessage = $"Query {queryId}: {ownershipError}";
            _logger.LogWarning("AI Actor {ActorId} was denied subscribing to query {QueryId}", actor.Id, queryId);
            return;
        }

        // With the approval workflow on, an actor schedules only SQL a second person approved: the query's active version,
        // approved for exactly its content (a query it just created waits for approval, and is subscribed to once approved).
        if (_configuration.ApprovalWorkflow.Enabled)
        {
            var approved = await SavedQueryRunnableVersion.ForQuery(context, queryId.Value, requireApprovedHash: true)
                .AnyAsync(cancellationToken);
            if (!approved)
            {
                action.ErrorMessage = $"Query {queryId}: {NotApprovedForSubscriptionMessage}";
                _logger.LogWarning("AI Actor {ActorId} was refused a subscription to query {QueryId}, which has no approved version", actor.Id, queryId);
                return;
            }
        }

        var existingSubscriptions = await context.Subscriptions
            .Where(x => x.QueryId == queryId.Value)
            .CountAsync(cancellationToken);

        var capError = AiActorActionGuard.CheckSubscriptionCap(existingSubscriptions, actor.MaxSubscriptionsPerQuery);
        if (capError != null)
        {
            action.ErrorMessage = capError;
            _logger.LogWarning("AI Actor {ActorId} hit the subscription cap for query {QueryId}", actor.Id, queryId);
            return;
        }

        var subscription = new Subscription
        {
            QueryId = queryId.Value,
            CronExpression = cronExpression,
            NotificationTrigger = notificationTrigger,
            CreateTasks = true, // AI-created subscriptions create tasks by default
            ShowQuery = true,
            IncludeAttachment = false,
            AiActorId = actor.Id,
            // Owned and created by the actor's creator, whose access its runs act with.
            OwnerUserId = actor.CreatedByUserId,
            CreatedByUserId = actor.CreatedByUserId
        };

        context.Subscriptions.Add(subscription);
        await _securityAudit.SaveWithEventsAsync(
            context,
            () =>
            [
                new SecurityAuditEntry(SecurityAuditActions.SubscriptionCreated)
                    .On(SecurityAuditResources.Subscription, subscription.Id)
                    .With(new SecurityAuditDetails()
                        .Add("queryId", subscription.QueryId)
                        .Add("aiActorId", actor.Id)
                        .Add("createTasks", subscription.CreateTasks))
            ],
            cancellationToken);

        action.Success = true;
        action.ResultEntityId = subscription.Id;
    }

    private async Task ExecuteRefineQueryAsync(
        Beacon.Core.Data.Entities.AiActor actor,
        AiActorActionPlan plan,
        AiActorAction action,
        IReadOnlyList<string> contributors,
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
            .Include(x => x.Steps)
                .ThenInclude(x => x.Parameters)
            .Where(x => x.Id == queryId)
            .Where(x => x.AiActorId == actor.Id)
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

        // Checked before the stored step is touched, as a new query is (see ExecuteCreateQueryAsync).
        var rejection = await _saveCheck.RejectionAsync(firstStep.DataSourceId, newSql, [], cancellationToken);
        if (rejection != null)
        {
            action.ErrorMessage = $"Refined query validation failed: {rejection}";
            _logger.LogWarning("AI Actor {ActorId} proposed a refinement of query {QueryId} the save check refused", actor.Id, queryId);
            return;
        }

        var previousSql = firstStep.SqlValue;
        var changeReason = plan.Parameters.GetValueOrDefault("reason")?.ToString();

        // The refined query: the stored steps, the first one with the new SQL.
        var refinedSteps = query.Steps
            .OrderBy(x => x.StepOrder)
            .Select(x =>
                new QueryStepData
                {
                    StepId = x.Id,
                    StepOrder = x.StepOrder,
                    Name = x.Name ?? string.Empty,
                    Description = x.Description,
                    SqlValue = x == firstStep ? newSql : x.SqlValue,
                    DataSourceId = x.DataSourceId,
                    Parameters = x.Parameters
                        .Select(y =>
                            new QueryStepParameterData
                            {
                                Name = y.Name,
                                Type = y.Type,
                                Description = y.Description,
                                Placeholder = y.Placeholder
                            })
                        .ToList()
                })
            .ToList();

        // Validated before anything is stored — with the approval workflow on, without running it (DraftFailureAsync): the
        // stored query never runs the refined SQL before it is saved (or approved), and a refinement that fails changes
        // nothing.
        var failure = await DraftFailureAsync(queryId, refinedSteps, query.FinalQuery, cancellationToken);
        if (failure != null)
        {
            action.Success = false;
            action.ErrorMessage = $"Refined query validation failed: {failure}";
            _logger.LogWarning("AI Actor {ActorId} refined query {QueryId} with invalid SQL ({ErrorClass})",
                actor.Id, queryId, DatabaseErrorRedactor.ClassOnly(failure, _logger));
            return;
        }

        var reason = Truncated(changeReason ?? plan.Reasoning, MaxChangeReasonLength);

        // With the approval workflow on, the refinement is a change like a person's edit: submitted for approval, with the
        // actor's creator as its requester and the plan's reviewer and revision requesters recorded (so none of them can
        // approve it), and live only once approved. The submission records the stored SQL it would replace when no version
        // holds it, and never replaces another person's pending change of the query (409, the action fails).
        if (_configuration.ApprovalWorkflow.Enabled)
        {
            await _versionService.SubmitForApprovalAsync(
                queryId,
                new QueryData
                {
                    QueryId = queryId,
                    Name = query.Name,
                    Description = query.Description,
                    FinalQuery = query.FinalQuery,
                    Steps = refinedSteps
                },
                new QueryChangeSubmission(actor.CreatedByUserId, null, AiActorChangeSource, reason, "AI actor refinement")
                {
                    ContributorUserIds = contributors
                },
                cancellationToken);

            _logger.LogInformation("AI Actor {ActorId} submitted a refinement of query {QueryId} for approval", actor.Id, queryId);

            action.Success = true;
            action.ResultEntityId = queryId;
            return;
        }

        // Without the workflow, a versioned save: history keeps the SQL it replaces, and the refinement becomes the active
        // version, attributed to the actor's creator.
        await _versionService.EnsureBaselineVersionAsync(queryId, cancellationToken);

        firstStep.SqlValue = newSql;
        query.ModifiedByUserId = actor.CreatedByUserId;
        query.ModifiedTime = DateTime.UtcNow;
        context.QueryStepChangeHistory.Add(new QueryStepChangeHistory
        {
            QueryStepId = firstStep.Id,
            DataSourceId = firstStep.DataSourceId,
            AiActorId = actor.Id,
            AiActorExecutionId = executionId,
            AiActorPlanId = planId,
            PreviousSql = previousSql,
            NewSql = newSql,
            ChangeReason = changeReason ?? plan.Reasoning,
            ChangeSource = ChangeSource.AiActor,
            ChangedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync(cancellationToken);

        await _versionService.CreateVersionAsync(queryId, actor.CreatedByUserId, AiActorChangeSource, reason, QueryVersionStatus.Active, cancellationToken);

        _logger.LogInformation("AI Actor {ActorId} refined query {QueryId}", actor.Id, queryId);

        action.Success = true;
        action.ResultEntityId = queryId;
    }

    // Why the proposed steps would not run, or null when they would. With the approval workflow on nothing runs: SQL nobody
    // reviewed is checked as a save checks it and by its source's own non-executing validation (IQueryProposalCheck: a plan
    // or a description of the result, never a row). Without the workflow the draft runs as an editor's preview does (the
    // error it reports, or what it threw).
    private async Task<string?> DraftFailureAsync(int queryId, List<QueryStepData> steps, string? finalQuery, CancellationToken cancellationToken)
    {
        var draft = new QueryDraft
        {
            Steps = steps,
            FinalQuery = finalQuery
        };

        try
        {
            if (_configuration.ApprovalWorkflow.Enabled)
            {
                return await _proposalCheck.RejectionAsync(draft, cancellationToken);
            }

            var result = await _queryService.PreviewQuery(queryId, draft, ValidationPage.Instance, cancellationToken);

            return result.Success ? null : result.ErrorMessage ?? "Unknown error";
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ex.Message;
        }
    }

    private static string? Truncated(string? value, int maxLength)
    {
        return value == null || value.Length <= maxLength ? value : value[..maxLength];
    }

    // The smallest page: validation needs to know the SQL runs, not its rows.
    private sealed record ValidationPage : ListRequest
    {
        public static ValidationPage Instance { get; } = new() { Page = 0, PageSize = 1 };
    }

    private async Task ExecuteArchiveQueryAsync(
        Beacon.Core.Data.Entities.AiActor actor,
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

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var target = await context.Queries
            .Where(x => x.Id == queryId)
            .Select(x =>
                new
                {
                    x.AiActorId,
                    x.IsLocked
                })
            .FirstOrDefaultAsync(cancellationToken);

        if (target == null)
        {
            action.ErrorMessage = $"Query {queryId} not found";
            return;
        }

        var ownershipError = AiActorActionGuard.CheckOwnership(actor.Id, target.AiActorId, target.IsLocked);
        if (ownershipError != null)
        {
            action.ErrorMessage = $"Query {queryId}: {ownershipError}";
            _logger.LogWarning("AI Actor {ActorId} was denied archiving query {QueryId}", actor.Id, queryId);
            return;
        }

        await _queryService.DeleteQuery(queryId, cancellationToken);

        action.Success = true;
        action.ResultEntityId = queryId;
    }

    private async Task ExecuteArchiveSubscriptionAsync(
        Beacon.Core.Data.Entities.AiActor actor,
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

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var owner = await context.Subscriptions
            .Where(x => x.Id == subscriptionId)
            .Select(x =>
                new
                {
                    x.AiActorId
                })
            .FirstOrDefaultAsync(cancellationToken);

        if (owner == null)
        {
            action.ErrorMessage = $"Subscription {subscriptionId} not found";
            return;
        }

        var ownershipError = AiActorActionGuard.CheckOwnership(actor.Id, owner.AiActorId, false);
        if (ownershipError != null)
        {
            action.ErrorMessage = $"Subscription {subscriptionId}: {ownershipError}";
            _logger.LogWarning("AI Actor {ActorId} was denied archiving subscription {SubscriptionId}", actor.Id, subscriptionId);
            return;
        }

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
            await _securityAudit.SaveWithEventsAsync(
                context,
                () =>
                [
                    new SecurityAuditEntry(SecurityAuditActions.AiPlanSubmitted)
                        .On(SecurityAuditResources.AiActorPlan, plan.Id)
                        .With(new SecurityAuditDetails()
                            .Add("aiActorId", plan.AiActorId)
                            .Add("parentPlanId", plan.ParentPlanId)
                            .Add("version", plan.Version)
                            .Add("proposedActionCount", proposedActions.Count))
                ],
                cancellationToken);

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
            _logger.LogError("Failed to generate plan for actor {ActorId} ({Failure})", options.ActorId, FailureCodes.Describe(ex));
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

        var actor = plan.AiActor;

        // Decided only from PendingApproval, in one conditional update: a second decision, or one racing this, changes
        // nothing and is refused.
        var reviewedAt = DateTime.UtcNow;
        await DecidePlanAsync(context, options.PlanId, AiActorPlanStatus.Executing, options.UserId, options.Comment, reviewedAt, cancellationToken);
        plan.Status = AiActorPlanStatus.Executing;
        plan.ReviewedAt = reviewedAt;
        plan.ReviewedByUserId = options.UserId;
        plan.ReviewerComment = options.Comment;

        // Who shaped the query changes this approval submits, besides the actor's creator: the plan's reviewer and whoever
        // asked for its revisions. None of them approves those changes.
        var contributors = await AiActorPlanChain.RevisionRequestersAsync(context, plan.Id, cancellationToken);
        if (options.UserId != null)
        {
            contributors = [options.UserId, .. contributors];
        }

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

                var action = await ExecuteActionAsync(actor, actionPlan, newlyCreatedQueries, cancellationToken, execution.Id, plan.Id, contributors);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RecordCancelledAsync(context, execution);
            throw;
        }
        catch (Exception ex)
        {
            execution.Phase = AiActorExecutionPhase.Failed;
            execution.ErrorMessage = ex.Message;
            execution.CompletedAt = DateTime.UtcNow;

            plan.Status = AiActorPlanStatus.Rejected;

            await TrySaveFailureAsync(context);

            _logger.LogError("Failed to execute approved plan {PlanId} ({Failure})", options.PlanId, FailureCodes.Describe(ex));
            return AiActorThinkResult.CreateFailure(execution.Id, ex.Message);
        }
    }

    public async Task RejectPlanAsync(
        RejectPlanOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        await DecidePlanAsync(context, options.PlanId, AiActorPlanStatus.Rejected, options.UserId, options.Reason, DateTime.UtcNow, cancellationToken);

        _logger.LogInformation("Rejected plan {PlanId}", options.PlanId);
    }

    public async Task<AiActorPlanResult> RequestPlanRevisionAsync(
        RequestRevisionOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var plan = await context.AiActorPlans
            .Where(x => x.Id == options.PlanId)
            .Select(x =>
                new
                {
                    x.AiActorId,
                    x.UserInstruction
                })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconException($"Plan with ID {options.PlanId} not found");

        // Mark original plan as revision requested, only from PendingApproval: the person asking is recorded on it, and
        // the revised plan's chain names them.
        await DecidePlanAsync(context, options.PlanId, AiActorPlanStatus.RevisionRequested, options.UserId, options.Feedback, DateTime.UtcNow, cancellationToken);

        // Generate new plan with the feedback
        var newPlanOptions = new GeneratePlanOptions
        {
            ActorId = plan.AiActorId,
            UserInstruction = $"{plan.UserInstruction}\n\nRevision feedback: {options.Feedback}",
            ParentPlanId = options.PlanId,
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

    // A plan is approved, rejected or sent back for revision only from PendingApproval, as one conditional update that
    // records who decided: a decision on a plan that is no longer pending (decided, or decided concurrently) changes
    // nothing and is refused.
    private async Task DecidePlanAsync(
        BeaconContext context,
        int planId,
        AiActorPlanStatus status,
        string? reviewerUserId,
        string? comment,
        DateTime reviewedAt,
        CancellationToken cancellationToken)
    {
        // The decision and its security audit event commit together; the reviewer is the event's actor, the comment stays
        // on the plan.
        var action = status switch
        {
            AiActorPlanStatus.Rejected => SecurityAuditActions.AiPlanRejected,
            AiActorPlanStatus.RevisionRequested => SecurityAuditActions.AiPlanRevisionRequested,
            _ => SecurityAuditActions.AiPlanApproved
        };
        var decided = await _securityAudit.SaveWithEventsAsync(
            context,
            () => context.AiActorPlans
                .Where(x => x.Id == planId)
                .Where(x => x.Status == AiActorPlanStatus.PendingApproval)
                .ExecuteUpdateAsync(
                    x => x
                        .SetProperty(y => y.Status, status)
                        .SetProperty(y => y.ReviewedAt, reviewedAt)
                        .SetProperty(y => y.ReviewedByUserId, reviewerUserId)
                        .SetProperty(y => y.ReviewerComment, comment),
                    cancellationToken),
            written => written == 0
                ? []
                :
                [
                    new SecurityAuditEntry(action)
                        .On(SecurityAuditResources.AiActorPlan, planId)
                        .With(new SecurityAuditDetails().Add("outcome", status))
                ],
            cancellationToken);
        if (decided == 0)
        {
            throw new BeaconConflictException(PlanNotPendingMessage);
        }
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

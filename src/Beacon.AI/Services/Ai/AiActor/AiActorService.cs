using System.Data;
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
using Beacon.Core.Validators;
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
public partial class AiActorService : IAiActorServiceExtended
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

    /// <summary>Why a decision on a plan that expired before anyone decided it is refused.</summary>
    public const string PlanExpiredMessage = "This plan expired before it was decided: it can no longer be approved, rejected or revised.";

    /// <summary>
    /// Why an actor that requires approval does not refine, think or get a new plan while one of its plans waits for a
    /// decision; followed by the plan when it is known.
    /// </summary>
    public const string PlanPendingMessage = "This AI actor has a plan pending approval; decide it, or let it expire, first.";

    /// <summary>Why a revision was asked for but no revised plan was created; the reason follows.</summary>
    public const string RevisionNotCreatedMessage = "The plan was sent back for revision, but no revised plan was created.";

    /// <summary>Why a cycle's proposed actions were not submitted: another plan of the actor became pending meanwhile.</summary>
    public const string ProposalsNotSubmittedMessage = "The proposed actions were not submitted for approval: another plan of this AI actor is pending.";

    /// <summary>Why no plan was generated: the model proposed no action of a known type.</summary>
    public const string NoActionsProposedMessage = "The model proposed no action this AI actor can take; no plan was created.";

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
    private readonly ISubscriptionScheduleCheck _scheduleCheck;
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

    [Obsolete("Use the constructor that takes the schedule check, think cooldown options, time provider and think claim: without them the default schedule limits apply.")]
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
            contextFactory,
            llmProvider,
            metadataService,
            queryService,
            subscriptionService,
            beaconScheduler,
            runAs,
            executionAccess,
            saveCheck,
            proposalCheck,
            versionService,
            configuration,
            securityAudit,
            SubscriptionScheduleCheck.Default,
            logger,
            null,
            null,
            null)
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
        ISubscriptionScheduleCheck scheduleCheck,
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
        _scheduleCheck = scheduleCheck;
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
            CreatedByUserId = options.CreatedByUserId,
            RequiresApproval = options.RequiresApproval
        };

        // The creation is on record with whether the actor's changes wait for approval: an actor that does not runs them
        // as it decides them.
        context.AiActors.Add(actor);
        await _securityAudit.SaveWithEventsAsync(
            context,
            () =>
            [
                new SecurityAuditEntry(SecurityAuditActions.AiActorCreated)
                    .On(SecurityAuditResources.AiActor, actor.Id)
                    .With(new SecurityAuditDetails()
                        .Add("dataSourceId", actor.DataSourceId)
                        .Add("requiresApproval", actor.RequiresApproval))
            ],
            cancellationToken);

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

            return await RecordSkippedCycleAsync(context, actorId, triggeringSubscriptionId, startTime, OwnerNotAuthorizedMessage, cancellationToken);
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

        // An actor that requires approval proposes nothing new while one of its plans waits for a decision: the cycle is
        // recorded as skipped before any LLM call.
        if (actor.RequiresApproval && await PendingPlanIdAsync(context, actorId, cancellationToken) is { } pendingPlanId)
        {
            return await RecordSkippedCycleAsync(context, actorId, triggeringSubscriptionId, startTime, $"Skipped: {PendingPlanMessage(pendingPlanId)}", cancellationToken);
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

            var completion = await CompletePlanAsync(
                AiActorPrompts.ThinkCycleSystemPrompt,
                userPrompt,
                LlmOperations.ActorThink,
                background ? LlmRequestPriority.Background : LlmRequestPriority.Interactive,
                cancellationToken);
            RecordCompletion(execution, completion);
            var planResponse = completion.Plan;

            // 3. EXECUTING PHASE
            execution.Phase = AiActorExecutionPhase.Executing;
            await context.SaveChangesAsync(cancellationToken);

            var (executedActions, proposals) = await RunPlannedActionsAsync(actor, planResponse.Actions, execution, cancellationToken);

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

            var submission = await SaveCycleAsync(context, execution, planResponse, proposals, existingQueries, null, cancellationToken);

            var result = AiActorThinkResult.CreateSuccess(execution.Id, execution.DecisionSummary);
            result.PlanId = submission.PlanId;
            result.ProposalsNotSubmittedReason = submission.Refused ? ProposalsNotSubmittedMessage : null;
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

            DetachUnsavedProposal(context);
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

        // An actor that requires approval is not refined while one of its plans waits for a decision: refused before any
        // LLM call, naming the plan.
        if (actor.RequiresApproval && await PendingPlanIdAsync(context, actorId, cancellationToken) is { } pendingPlanId)
        {
            throw new BeaconConflictException(PendingPlanMessage(pendingPlanId));
        }

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

            var completion = await CompletePlanAsync(
                AiActorPrompts.RefinementSystemPrompt,
                userPrompt,
                LlmOperations.ActorRefine,
                LlmRequestPriority.Interactive,
                cancellationToken);
            var llmResponse = completion.Response;

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

            RecordCompletion(execution, completion);
            var planResponse = completion.Plan;

            // Execute actions
            execution.Phase = AiActorExecutionPhase.Executing;
            var (executedActions, proposals) = await RunPlannedActionsAsync(actor, planResponse.Actions, execution, cancellationToken);

            execution.Phase = AiActorExecutionPhase.Completed;
            execution.CompletedAt = DateTime.UtcNow;

            actor.TotalTokensUsed += execution.TokensUsed;
            actor.TotalCost += execution.EstimatedCost;

            var submission = await SaveCycleAsync(context, execution, planResponse, proposals, existingQueries, feedback, cancellationToken);
            if (submission.Refused)
            {
                // The refinement completed and is on record, but what it proposed waits nowhere: the caller is told.
                throw new BeaconConflictException(PendingPlanMessage(submission.PendingPlanId));
            }

            var result = AiActorThinkResult.CreateSuccess(execution.Id, execution.DecisionSummary);
            result.PlanId = submission.PlanId;
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
        catch (BeaconConflictException)
        {
            throw;
        }
        catch (Exception ex)
        {
            execution.Phase = AiActorExecutionPhase.Failed;
            execution.ErrorMessage = ex.Message;
            execution.CompletedAt = DateTime.UtcNow;
            DetachUnsavedProposal(context);
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

    private AiActorOptions CurrentOptions => _options?.CurrentValue ?? new AiActorOptions();

    // A plan proposed at or before this moment has expired: it is no longer pending, whatever its stored status says.
    private DateTime PlanExpiryCutoff() =>
        _timeProvider.GetUtcNow().UtcDateTime - TimeSpan.FromDays(CurrentOptions.PlanExpiryDays);

    // The end of a cycle. Its completion (execution, actor totals) is saved first, whatever becomes of its proposals. With
    // approval required, the actions the cycle proposed then wait for a person as one PendingApproval plan, admitted with
    // the execution's note of it and its submission event (AdmitPlanAsync); approving it runs them through the executor's
    // guards. Think and refine do not start while a plan is pending; a plan that became pending meanwhile (or a
    // concurrent admission) refuses this one: no plan, no event, and the execution notes the proposals were not submitted.
    private async Task<CycleSubmission> SaveCycleAsync(
        BeaconContext context,
        AiActorExecution execution,
        AiActorPlanResponse planResponse,
        IReadOnlyList<AiActorActionPlan> proposals,
        IReadOnlyList<AiActorPrompts.QueryContext> existingQueries,
        string? userInstruction,
        CancellationToken cancellationToken)
    {
        await context.SaveChangesAsync(cancellationToken);
        if (proposals.Count == 0)
        {
            return CycleSubmission.None;
        }

        // The cycle's tokens and cost stay on its execution, already in the actor's totals: approving a plan adds the plan's
        // own to them, so this plan carries none.
        var proposedActions = await ToProposedActionsAsync(context, proposals, existingQueries, cancellationToken);
        var plan = new AiActorPlan
        {
            AiActorId = execution.AiActorId,
            Status = AiActorPlanStatus.PendingApproval,
            UserInstruction = userInstruction,
            Analysis = planResponse.Analysis ?? string.Empty,
            FindingsJson = planResponse.Findings?.Count > 0
                ? JsonSerializer.Serialize(planResponse.Findings, JsonOptions)
                : null,
            ActionsJson = JsonSerializer.Serialize(proposedActions, JsonOptions),
            ProposedAt = _timeProvider.GetUtcNow().UtcDateTime,
            Model = execution.Model
        };

        var admission = await AdmitPlanAsync(
            context,
            plan,
            proposedActions.Count,
            () => execution.DetailedAnalysis = $"Proposed actions wait for approval in plan {plan.Id}.",
            cancellationToken);
        if (admission.Refused)
        {
            execution.DetailedAnalysis = admission.PendingPlanId is { } pendingPlanId
                ? $"{ProposalsNotSubmittedMessage} Plan {pendingPlanId}."
                : ProposalsNotSubmittedMessage;
            await context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Actor {ActorId} proposed {ActionCount} actions while another plan ({PlanId}) is pending; no plan was added",
                execution.AiActorId, proposals.Count, admission.PendingPlanId);

            return admission;
        }

        _logger.LogInformation("Actor {ActorId} proposed plan {PlanId} with {ActionCount} actions", execution.AiActorId, plan.Id, proposedActions.Count);

        return admission;
    }

    // Admits a new plan: whether the actor has a plan pending (and not expired) and the plan's insert, with its submission
    // event (the recorder joins the open transaction), run in one serializable transaction, so two producers (cycles,
    // refinements, revisions) can never both add one. A pending plan, or losing a serialization conflict to a concurrent
    // admission, refuses it: nothing of it is written. onSaved runs once the plan has its id, inside the transaction, for
    // changes that must commit with it.
    private async Task<CycleSubmission> AdmitPlanAsync(
        BeaconContext context,
        AiActorPlan plan,
        int proposedActionCount,
        Action? onSaved,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            if (await PendingPlanIdAsync(context, plan.AiActorId, cancellationToken) is { } pendingPlanId)
            {
                return CycleSubmission.RefusedBy(pendingPlanId);
            }

            context.AiActorPlans.Add(plan);
            await _securityAudit.SaveWithEventsAsync(
                context,
                () =>
                {
                    onSaved?.Invoke();

                    return
                    [
                        new SecurityAuditEntry(SecurityAuditActions.AiPlanSubmitted)
                            .On(SecurityAuditResources.AiActorPlan, plan.Id)
                            .With(new SecurityAuditDetails()
                                .Add("aiActorId", plan.AiActorId)
                                .Add("parentPlanId", plan.ParentPlanId)
                                .Add("version", plan.Version)
                                .Add("proposedActionCount", proposedActionCount))
                    ];
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return CycleSubmission.Admitted(plan.Id);
        }
        catch (Exception ex) when (DbSerializationConflict.IsSerializationConflict(ex))
        {
            // Rolled back: neither the plan nor its event exists, whatever the tracker says.
            DetachUnsavedProposal(context);
            _logger.LogInformation("A plan for actor {ActorId} lost a concurrent admission and was not added", plan.AiActorId);

            return CycleSubmission.RefusedBy(null);
        }
    }

    private static string PendingPlanMessage(int? pendingPlanId) =>
        pendingPlanId is { } id ? $"{PlanPendingMessage} Plan {id}." : PlanPendingMessage;

    // What became of a cycle's proposals: the plan admitted for them, or the refusal (and the pending plan, when known).
    private sealed record CycleSubmission(int? PlanId, bool Refused, int? PendingPlanId)
    {
        public static CycleSubmission None { get; } = new(null, false, null);

        public static CycleSubmission Admitted(int planId) => new(planId, false, null);

        public static CycleSubmission RefusedBy(int? pendingPlanId) => new(null, true, pendingPlanId);
    }

    // The newest plan of the actor still pending approval and not expired, if any.
    private async Task<int?> PendingPlanIdAsync(BeaconContext context, int actorId, CancellationToken cancellationToken)
    {
        var cutoff = PlanExpiryCutoff();

        return await context.AiActorPlans
            .Where(x => x.AiActorId == actorId)
            .Where(x => x.Status == AiActorPlanStatus.PendingApproval)
            .Where(x => x.ProposedAt > cutoff)
            .OrderByDescending(x => x.ProposedAt)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // Before a failed cycle's failure record is saved: a plan the cycle added and the security events staged for it are
    // not written with it (their save failed or was rolled back), so no event names a plan that does not exist.
    private static void DetachUnsavedProposal(BeaconContext context)
    {
        var proposal = context.ChangeTracker.Entries()
            .Where(x => x.Entity is AiActorPlan or SecurityAuditEvent)
            .ToList();
        foreach (var entry in proposal)
        {
            entry.State = EntityState.Detached;
        }
    }

    // A plan's actions as ApprovePlanAsync executes them: each action of a known type with its parameters, and for a
    // refinement the query it targets as the actor saw it (its SQL, whether it is locked). Other actions are not proposed.
    private static async Task<List<ProposedAction>> ToProposedActionsAsync(
        BeaconContext context,
        IEnumerable<AiActorActionPlan?> actions,
        IReadOnlyList<AiActorPrompts.QueryContext> existingQueries,
        CancellationToken cancellationToken)
    {
        var proposedActions = new List<ProposedAction>();
        foreach (var actionPlan in actions)
        {
            if (actionPlan == null)
            {
                continue;
            }

            var proposed = AiActorActionGuard.ToProposed(actionPlan);
            if (!proposed.Proposed)
            {
                continue;
            }

            var proposedAction = new ProposedAction
            {
                ActionType = proposed.ActionType,
                Reasoning = actionPlan.Reasoning ?? string.Empty,
                Parameters = actionPlan.Parameters
            };

            if (proposed.ActionType == AiActorActionType.RefineQuery && proposed.TargetQueryId is { } queryId)
            {
                var target = existingQueries
                    .Where(x => x.QueryId == queryId)
                    .FirstOrDefault();
                proposedAction = proposedAction with
                {
                    TargetQueryId = queryId,
                    TargetQueryName = target?.QueryName,
                    CurrentSql = target?.Sql,
                    ProposedSql = proposed.SqlQuery,
                    IsLocked = await context.Queries
                        .Where(x => x.Id == queryId)
                        .Where(x => x.IsLocked)
                        .AnyAsync(cancellationToken)
                };
            }

            proposedActions.Add(proposedAction);
        }

        return proposedActions;
    }

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

    // A cycle that does not run (its creator may no longer run it, or a plan of the actor waits for a decision): recorded
    // as a skipped execution (no LLM call, no SQL, no change to the actor), not as a failure.
    private static async Task<AiActorThinkResult> RecordSkippedCycleAsync(
        BeaconContext context,
        int actorId,
        int? triggeringSubscriptionId,
        DateTime startTime,
        string reason,
        CancellationToken cancellationToken)
    {
        var execution = new AiActorExecution
        {
            AiActorId = actorId,
            TriggeringSubscriptionId = triggeringSubscriptionId,
            Phase = AiActorExecutionPhase.Skipped,
            StartedAt = startTime,
            CompletedAt = DateTime.UtcNow,
            ErrorMessage = reason
        };
        context.AiActorExecutions.Add(execution);
        await context.SaveChangesAsync(cancellationToken);

        var result = AiActorThinkResult.CreateFailure(execution.Id, reason);
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

            execution.Phase = AiActorExecutionPhase.Planning;
            await context.SaveChangesAsync(cancellationToken);

            var completion = await CompletePlanAsync(
                AiActorPrompts.InitialSetupSystemPrompt,
                userPrompt,
                LlmOperations.ActorSetup,
                LlmRequestPriority.Interactive,
                cancellationToken);
            RecordCompletion(execution, completion);
            var planResponse = completion.Plan;

            execution.Phase = AiActorExecutionPhase.Executing;
            await context.SaveChangesAsync(cancellationToken);

            var (_, proposals) = await RunPlannedActionsAsync(actor, planResponse.Actions, execution, cancellationToken);

            execution.Phase = AiActorExecutionPhase.Completed;
            execution.CompletedAt = DateTime.UtcNow;

            // The initial setup does not count towards the think cooldown: LastThinkTime stays as it is.
            actor.ThinkCount++;
            actor.TotalTokensUsed += execution.TokensUsed;
            actor.TotalCost += execution.EstimatedCost;

            await SaveCycleAsync(context, execution, planResponse, proposals, [], null, cancellationToken);

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
            DetachUnsavedProposal(context);
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

    // One completion of an actor prompt, through the provider (and so the LLM request queue), with its reply read as a plan.
    // Every producer asks with the same sampling settings; only the prompts, the operation tag and the lane differ.
    private async Task<PlanCompletion> CompletePlanAsync(
        string systemPrompt,
        string userPrompt,
        string operation,
        LlmRequestPriority priority,
        CancellationToken cancellationToken)
    {
        var llmRequest = new LlmRequest
        {
            SystemPrompt = systemPrompt,
            Messages = new List<ChatMessage>
            {
                new(ConversationRole.User, userPrompt)
            },
            Temperature = 0.3m,
            MaxTokens = 4096,
            Priority = priority,
            Operation = operation
        };

        var llmResponse = await _llmProvider.CompleteAsync(llmRequest, cancellationToken);

        return new PlanCompletion(llmResponse, ParseLlmResponse(llmResponse.Content));
    }

    // A cycle's execution carries its completion's usage and the model's analysis.
    private static void RecordCompletion(AiActorExecution execution, PlanCompletion completion)
    {
        execution.TokensUsed = completion.Response.TotalTokens;
        execution.EstimatedCost = completion.Response.Cost;
        execution.Model = completion.Response.Model;
        execution.DecisionSummary = completion.Plan.Analysis;
    }

    // The actions the model planned, in order, each executed or (with approval required) proposed through
    // ExecuteOrProposeAsync; a query an earlier action created is known to the later ones by name. What succeeded is counted
    // on the execution, and what became of every action is stored on it.
    private async Task<PlannedActionsOutcome> RunPlannedActionsAsync(
        Beacon.Core.Data.Entities.AiActor actor,
        IEnumerable<AiActorActionPlan> actions,
        AiActorExecution execution,
        CancellationToken cancellationToken)
    {
        var executedActions = new List<AiActorAction>();
        var proposals = new List<AiActorActionPlan>();
        var newlyCreatedQueries = new Dictionary<string, int>();

        foreach (var actionPlan in actions)
        {
            var action = await ExecuteOrProposeAsync(actor, actionPlan, newlyCreatedQueries, cancellationToken);
            executedActions.Add(action);
            if (action.Proposed)
            {
                proposals.Add(actionPlan);
            }

            RecordActionOutcome(execution, action, newlyCreatedQueries);
        }

        execution.ActionsJson = JsonSerializer.Serialize(executedActions, JsonOptions);

        return new PlannedActionsOutcome(executedActions, proposals);
    }

    // After an action ran: a query it created is known to later actions by name, and its success is counted on the execution.
    private static void RecordActionOutcome(AiActorExecution execution, AiActorAction action, Dictionary<string, int> newlyCreatedQueries)
    {
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

    private sealed record PlanCompletion(LlmResponse Response, AiActorPlanResponse Plan);

    private sealed record PlannedActionsOutcome(List<AiActorAction> Executed, List<AiActorActionPlan> Proposals);

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


    private async Task<int> GetNextConversationTurnAsync(int actorId, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var maxTurn = await context.AiActorConversations
            .Where(c => c.AiActorId == actorId)
            .MaxAsync(c => (int?)c.TurnNumber, cancellationToken);

        return (maxTurn ?? 0) + 1;
    }

    #endregion
}

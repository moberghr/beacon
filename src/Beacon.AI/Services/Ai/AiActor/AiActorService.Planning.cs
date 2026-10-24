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

public partial class AiActorService
{
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
                MaxTokens = 4096,
                Operation = LlmOperations.ActorPlan
            };

            var llmResponse = await _llmProvider.CompleteAsync(llmRequest, cancellationToken);
            var planResponse = ParseLlmResponse(llmResponse.Content);

            // Convert actions to ProposedAction list with additional context
            var proposedActions = await ToProposedActionsAsync(context, planResponse.Actions, existingQueries, cancellationToken);
            if (proposedActions.Count == 0)
            {
                _logger.LogInformation("No plan was generated for actor {ActorId}: the model proposed no action of a known type", actor.Id);

                return AiActorPlanResult.CreateFailure(NoActionsProposedMessage);
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
                ProposedAt = _timeProvider.GetUtcNow().UtcDateTime,
                TokensUsed = llmResponse.TotalTokens,
                EstimatedCost = llmResponse.Cost,
                Model = llmResponse.Model,
                Version = options.ParentPlanId.HasValue ? await GetNextPlanVersionAsync(options.ParentPlanId.Value, cancellationToken) : 1,
                ParentPlanId = options.ParentPlanId
            };

            var admission = await AdmitPlanAsync(context, plan, proposedActions.Count, null, cancellationToken);
            if (admission.Refused)
            {
                throw new BeaconConflictException(PendingPlanMessage(admission.PendingPlanId));
            }

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
        catch (BeaconConflictException)
        {
            throw;
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
        var reviewedAt = _timeProvider.GetUtcNow().UtcDateTime;
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

        await DecidePlanAsync(context, options.PlanId, AiActorPlanStatus.Rejected, options.UserId, options.Reason, _timeProvider.GetUtcNow().UtcDateTime, cancellationToken);

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
        await DecidePlanAsync(context, options.PlanId, AiActorPlanStatus.RevisionRequested, options.UserId, options.Feedback, _timeProvider.GetUtcNow().UtcDateTime, cancellationToken);

        // Generate new plan with the feedback
        var newPlanOptions = new GeneratePlanOptions
        {
            ActorId = plan.AiActorId,
            UserInstruction = $"{plan.UserInstruction}\n\nRevision feedback: {options.Feedback}",
            ParentPlanId = options.PlanId,
            PreviousFeedback = options.Feedback
        };

        try
        {
            return await GeneratePlanAsync(newPlanOptions, cancellationToken);
        }
        catch (BeaconConflictException ex)
        {
            // The original plan is decided already; the revision itself was refused (another plan is pending).
            throw new BeaconConflictException($"{RevisionNotCreatedMessage} {ex.Message}");
        }
    }

    public async Task<List<PendingPlanSummary>> GetPendingPlansAsync(
        int actorId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // An expired plan is no longer pending: it can no longer be approved or revised.
        var cutoff = PlanExpiryCutoff();
        var options = CurrentOptions;

        return await context.AiActorPlans
            .Where(p => p.AiActorId == actorId && p.Status == AiActorPlanStatus.PendingApproval)
            .Where(x => x.ProposedAt > cutoff)
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
                ExpiresAt = options.PlanExpiresAt(p.ProposedAt),
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

    // A plan is approved, rejected or sent back for revision only from PendingApproval and before it expired, as one
    // conditional update that records who decided: a decision on a plan that is no longer pending (decided, decided
    // concurrently, or expired) changes nothing and is refused.
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
        var cutoff = PlanExpiryCutoff();
        var decided = await _securityAudit.SaveWithEventsAsync(
            context,
            () => context.AiActorPlans
                .Where(x => x.Id == planId)
                .Where(x => x.ProposedAt > cutoff)
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
            // Nothing changed: say whether the plan expired undecided or was decided already.
            var expired = await context.AiActorPlans
                .Where(x => x.Id == planId)
                .Where(x => x.Status == AiActorPlanStatus.PendingApproval)
                .Where(x => x.ProposedAt <= cutoff)
                .AnyAsync(cancellationToken);

            throw new BeaconConflictException(expired ? PlanExpiredMessage : PlanNotPendingMessage);
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

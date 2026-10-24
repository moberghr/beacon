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
    // Used by the think / refine / initial-setup loops. Actors that require approval only record
    // what they would do (the cycle then saves its proposals as a plan, SaveCycleAsync); the approve path calls
    // ExecuteActionAsync directly.
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

        // The schedule an actor proposes is held to the same limits as one a person saves.
        try
        {
            _scheduleCheck.EnsureAllowed(cronExpression);
        }
        catch (InvalidOperationException ex)
        {
            action.ErrorMessage = ex.Message;
            _logger.LogWarning("AI Actor {ActorId} was refused a subscription schedule for query {QueryId}", actor.Id, queryId);
            return;
        }

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

    internal const string LegacyPlaceholderRefusal =
        "The query has a parameter placeholder in an old format; a person must edit the query before it can be refined.";

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

        // A save would refuse a stored placeholder that is not {identifier}, and the model cannot repair it: stop here so the
        // actor neither spends a model call on it nor retries.
        var hasLegacyPlaceholder = query.Steps
            .SelectMany(x => x.Parameters)
            .Any(x => !QueryParameterPlaceholder.IsValid(x.Placeholder));
        if (hasLegacyPlaceholder)
        {
            action.ErrorMessage = LegacyPlaceholderRefusal;
            _logger.LogWarning("AI Actor {ActorId} cannot refine query {QueryId}: a stored parameter placeholder has an old format", actor.Id, queryId);
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

            // The workflow-off save writes the steps itself, so a save's parameter rule (placeholder shape, distinct, in
            // the SQL) is applied here; the workflow-on check above applies it through the proposal check.
            foreach (var step in steps)
            {
                QueryValidator.CheckForParameters(
                    step.SqlValue,
                    step.Parameters
                        .Select(x =>
                            new QueryParameterData
                            {
                                Name = x.Name,
                                Type = x.Type,
                                Description = x.Description ?? "",
                                Placeholder = x.Placeholder ?? ""
                            })
                        .ToList());
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
}

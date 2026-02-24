using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Semantico.Core.Adapters;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers.File;
using Semantico.Core.Services;

namespace Semantico.Core.Worker.Services;

internal class JobService(
    IDbContextFactory<SemanticoContext> contextFactory,
    IQueryService queryService,
    INotificationService notificationService,
    ITaskService taskService,
    IAnomalyDetectionService anomalyDetectionService,
    IDataQualityEvaluationService dataQualityEvaluationService,
    ILogger<JobService> logger,
    IAiActorService? aiActorService = null)
    : IJobService
{
    // AI Actor service is optional - only available if Semantico.AI is added

    public async Task ExecuteQuery(int subscriptionId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var subscription = await context.Subscriptions
            .Where(x => x.Id == subscriptionId)
            .FirstOrDefaultAsync();

        if (subscription == null)
        {
            return;
        }

        var queryResult = await queryService.ExecuteQuery(subscriptionId, CancellationToken.None);

        // Set subscription specific parameters
        queryResult.ShowQuery = subscription.ShowQuery;
        queryResult.MaxRows = subscription.MaxRows;

        // Apply max rows limit if specified
        if (subscription.MaxRows.HasValue && subscription.MaxRows > 0)
        {
            queryResult.AllRecords = queryResult.AllRecords.Take(subscription.MaxRows.Value).ToList();
            queryResult.TopRecords = queryResult.TopRecords.Take(subscription.MaxRows.Value).ToList();
        }

        var lastExecutedQuery = context.QueryExecutionHistory
            .Where(x => x.SubscriptionId == subscriptionId)
            .OrderByDescending(x => x.CreatedTime)
            .Select(x =>
                new
                {
                    x.ResultCount
                })
            .FirstOrDefault();

        // Check if anomaly detection is enabled for this subscription
        var hasAnomalyDetection = await context.AnomalyConfigs
            .AnyAsync(x => x.SubscriptionId == subscriptionId && x.Enabled);

        Models.Anomaly.AnomalyEvaluationResult? anomalyEvaluation = null;

        // Only evaluate anomaly detection if it's enabled
        if (hasAnomalyDetection)
        {
            anomalyEvaluation = await anomalyDetectionService.EvaluateAnomalyAsync(
                subscriptionId,
                queryResult.TotalRecords,
                CancellationToken.None);

            // Store baseline for future anomaly detection
            await anomalyDetectionService.StoreBaselineAsync(
                subscriptionId,
                queryResult.TotalRecords,
                DateTime.UtcNow,
                CancellationToken.None);
        }

        var status = DetermineNotificationStatus(
            queryResult,
            subscription,
            lastExecutedQuery?.ResultCount,
            hasAnomalyDetection,
            anomalyEvaluation,
            subscriptionId);

        var executedQuery = new QueryExecutionHistory
        {
            SubscriptionId = subscriptionId,
            ResultCount = queryResult.TotalRecords,
            CompiledSql = queryResult.SqlQuery,
            NotificationStatus = status,
            ExecutionTimeMs = queryResult.ExecutionTimeMs,
            Results = subscription.StoreResults ? queryResult.QueryResults : null
        };

        await context.QueryExecutionHistory.AddAsync(executedQuery);

        // Handle tasks for subscriptions with CreateTasks enabled (even if no notifications to send)
        // This runs regardless of NotificationStatus to handle auto-resolve on 0 results
        if (subscription.CreateTasks)
        {
            await context.SaveChangesAsync(); // Save QueryExecutionHistory first

            logger.LogDebug("Creating/updating task for subscription {SubscriptionId}, result count {ResultCount}",
                subscriptionId, queryResult.TotalRecords);
            await taskService.CreateOrUpdateTask(
                subscriptionId,
                queryResult.TotalRecords,
                CancellationToken.None
            );
        }

        // Only send notification if the status is NotificationSent
        if (executedQuery.NotificationStatus != NotificationStatus.NotificationSent)
        {
            if (!subscription.CreateTasks) // Only save if we didn't already save above
            {
                await context.SaveChangesAsync();
            }

            // Trigger AI Actor think cycle even if no notification was sent
            await TriggerAiActorIfApplicableAsync(subscriptionId, queryResult.TotalRecords);
            return;
        }

        // Create Notification records for each recipient that was notified
        var notifications = new List<Notification>();
        foreach (var recipient in queryResult.Recipients)
        {
            var notification = new Notification
            {
                RecipientId = recipient.RecipientId.Value,
                Type = recipient.NotificationType,
                SentAt = DateTime.UtcNow,
                Results = queryResult.SaveResults ? queryResult.QueryResults : null
            };

            executedQuery.Notifications.Add(notification);
            notifications.Add(notification);
        }

        await context.SaveChangesAsync();

        // Record anomaly event if anomaly was detected
        if (anomalyEvaluation?.IsAnomaly == true)
        {
            await anomalyDetectionService.RecordAnomalyEventAsync(
                subscriptionId,
                anomalyEvaluation,
                notifications.FirstOrDefault()?.Id,
                CancellationToken.None);
        }

        var recipientsQueryResults = new List<RecipientQueryResult>();
        QueryResultFile? resultFile = null;

        // Only create attachment if subscription has attachments enabled and a file type is specified
        if (subscription.IncludeAttachment && subscription.ResultAttachmentType.HasValue)
        {
            resultFile = await ExportProvider.GetReport(subscription.ResultAttachmentType.Value, queryResult.AllRecords);
        }

        // TODO: refactor this to use sending Notifications table

        for (int i = 0; i < queryResult.Recipients.Count; i++)
        {
            var recipient = queryResult.Recipients[i];
            recipientsQueryResults.Add(new RecipientQueryResult
            {
                RecipientDestination = recipient.Destination,
                RecipientNotificationType = recipient.NotificationType,
                QueryResult = queryResult,
                QueryResultFile = resultFile,
                NotificationId = notifications[i].Id,
                AnomalyEvaluation = anomalyEvaluation?.IsAnomaly == true ? anomalyEvaluation : null,
                HeadersJson = recipient.HeadersJson,
                BodyTemplate = recipient.BodyTemplate
            });
        }

        try
        {
            foreach (var recipientQueryResult in recipientsQueryResults)
            {
                await notificationService.SendNotification(recipientQueryResult, lastExecutedQuery?.ResultCount);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send notification for subscription {SubscriptionId}", subscriptionId);
            executedQuery.NotificationStatus = NotificationStatus.Failed;
            executedQuery.Comment = ex.Message;
            await context.SaveChangesAsync();
            throw;
        }

        // Trigger AI Actor think cycle if this subscription belongs to an actor
        await TriggerAiActorIfApplicableAsync(subscriptionId, queryResult.TotalRecords);
    }

    public async Task EvaluateDataContract(int contractId)
    {
        try
        {
            await dataQualityEvaluationService.EvaluateContractAsync(contractId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to evaluate data contract {ContractId}", contractId);
            throw;
        }
    }

    private async Task TriggerAiActorIfApplicableAsync(int subscriptionId, int rowCount)
    {
        // AI Actor service is optional - only available if Semantico.AI is added
        if (aiActorService == null)
        {
            return;
        }

        try
        {
            await aiActorService.OnSubscriptionExecutedAsync(subscriptionId, rowCount, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Log but don't fail the subscription execution
            logger.LogWarning(ex, "Failed to trigger AI Actor for subscription {SubscriptionId}", subscriptionId);
        }
    }

    private NotificationStatus DetermineNotificationStatus(
        QueryResult queryResult,
        Subscription subscription,
        int? lastResultCount,
        bool hasAnomalyDetection,
        Models.Anomaly.AnomalyEvaluationResult? anomalyEvaluation,
        int subscriptionId)
    {
        // Check for timeout
        if (queryResult.TimedOut)
            return NotificationStatus.Timeout;

        // Check for no results
        if (queryResult.TotalRecords == 0)
            return NotificationStatus.NoResults;

        // Check minimum row count threshold
        if (subscription.MinimumRowCount.HasValue && queryResult.TotalRecords < subscription.MinimumRowCount.Value)
        {
            logger.LogDebug("Result count {ResultCount} is below minimum threshold {MinimumRowCount} for subscription {SubscriptionId}, notification silenced",
                queryResult.TotalRecords, subscription.MinimumRowCount.Value, subscriptionId);
            return NotificationStatus.BelowThreshold;
        }

        // Check anomaly detection
        if (hasAnomalyDetection)
        {
            if (anomalyEvaluation!.IsAnomaly)
            {
                logger.LogInformation("Anomaly detected for subscription {SubscriptionId}: {Explanation}",
                    subscriptionId, anomalyEvaluation.Explanation);
                return NotificationStatus.NotificationSent;
            }

            logger.LogDebug("No anomaly detected for subscription {SubscriptionId}, notification silenced", subscriptionId);
            return NotificationStatus.NotificationSilenced;
        }

        // Use notification trigger setting
        return subscription.NotificationTrigger switch
        {
            NotificationTrigger.Always => NotificationStatus.NotificationSent,
            NotificationTrigger.OnResultCountChange when lastResultCount == null => NotificationStatus.NotificationSent,
            NotificationTrigger.OnResultCountChange when queryResult.TotalRecords != lastResultCount => NotificationStatus.NotificationSent,
            NotificationTrigger.OnResultCountIncrease when lastResultCount == null => NotificationStatus.NotificationSent,
            NotificationTrigger.OnResultCountIncrease when queryResult.TotalRecords > lastResultCount => NotificationStatus.NotificationSent,
            _ => NotificationStatus.NotificationSilenced
        };
    }
}
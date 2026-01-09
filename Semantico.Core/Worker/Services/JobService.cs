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
    ILogger<JobService> logger)
    : IJobService
{
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

        NotificationStatus status;

        // Use the explicit TimedOut flag
        if (queryResult.TimedOut)
        {
            status = NotificationStatus.Timeout;
        }
        else if (queryResult.TotalRecords == 0)
        {
            status = NotificationStatus.NoResults;
        }
        else if (hasAnomalyDetection)
        {
            // If anomaly detection is enabled, ONLY send notification if anomaly is detected
            // Do not fall through to old logic
            if (anomalyEvaluation!.IsAnomaly)
            {
                status = NotificationStatus.NotificationSent;
                logger.LogInformation("Anomaly detected for subscription {SubscriptionId}: {Explanation}",
                    subscriptionId, anomalyEvaluation.Explanation);
            }
            else
            {
                status = NotificationStatus.NotificationSilenced;
                logger.LogDebug("No anomaly detected for subscription {SubscriptionId}, notification silenced",
                    subscriptionId);
            }
        }
        else
        {
            // Use NotificationTrigger setting to determine if notification should be sent
            status = subscription.NotificationTrigger switch
            {
                NotificationTrigger.Always => NotificationStatus.NotificationSent,
                NotificationTrigger.OnResultCountChange when lastExecutedQuery == null => NotificationStatus.NotificationSent,
                NotificationTrigger.OnResultCountChange when queryResult.TotalRecords != lastExecutedQuery.ResultCount => NotificationStatus.NotificationSent,
                NotificationTrigger.OnResultCountIncrease when lastExecutedQuery == null => NotificationStatus.NotificationSent,
                NotificationTrigger.OnResultCountIncrease when queryResult.TotalRecords > lastExecutedQuery.ResultCount => NotificationStatus.NotificationSent,
                _ => NotificationStatus.NotificationSilenced
            };
        }

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
                AnomalyEvaluation = anomalyEvaluation?.IsAnomaly == true ? anomalyEvaluation : null
            });
        }

        foreach (var recipientQueryResult in recipientsQueryResults)
        {
            await notificationService.SendNotification(recipientQueryResult, lastExecutedQuery?.ResultCount);
        }
    }
}
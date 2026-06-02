using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Adapters;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers.File;
using Beacon.Core.Services;

namespace Beacon.Core.Worker.Services;

internal class JobService(
    IDbContextFactory<BeaconContext> contextFactory,
    IQueryService queryService,
    INotificationService notificationService,
    ITaskService taskService,
    IAnomalyDetectionService anomalyDetectionService,
    IDataQualityEvaluationService dataQualityEvaluationService,
    ILogger<JobService> logger,
    IAiActorService? aiActorService = null,
    IMcpLearningAggregationService? mcpLearningService = null)
    : IJobService
{
    // AI Actor service is optional - only available if Beacon.AI is added

    public async Task ExecuteQuery(int subscriptionId, IJobCancellationToken cancellationToken)
    {
        var ct = cancellationToken.ShutdownToken;

        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var subscription = await context.Subscriptions
            .Where(x => x.Id == subscriptionId)
            .FirstOrDefaultAsync(ct);

        if (subscription == null)
        {
            return;
        }

        var queryResult = await queryService.ExecuteQuery(subscriptionId, ct);

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
            .Where(x => x.SubscriptionId == subscriptionId)
            .Where(x => x.Enabled)
            .AnyAsync(ct);

        Models.Anomaly.AnomalyEvaluationResult? anomalyEvaluation = null;

        // Only evaluate anomaly detection if it's enabled
        if (hasAnomalyDetection)
        {
            anomalyEvaluation = await anomalyDetectionService.EvaluateAnomalyAsync(
                subscriptionId,
                queryResult.TotalRecords,
                ct);

            // Store baseline for future anomaly detection
            await anomalyDetectionService.StoreBaselineAsync(
                subscriptionId,
                queryResult.TotalRecords,
                DateTime.UtcNow,
                ct);
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

        await context.QueryExecutionHistory.AddAsync(executedQuery, ct);

        // Handle tasks for subscriptions with CreateTasks enabled (even if no notifications to send)
        // This runs regardless of NotificationStatus to handle auto-resolve on 0 results
        if (subscription.CreateTasks)
        {
            await context.SaveChangesAsync(ct); // Save QueryExecutionHistory first

            logger.LogDebug("Creating/updating task for subscription {SubscriptionId}, result count {ResultCount}",
                subscriptionId, queryResult.TotalRecords);
            await taskService.CreateOrUpdateTask(
                subscriptionId,
                queryResult.TotalRecords,
                ct
            );
        }

        // Only send notification if the status is NotificationSent
        if (executedQuery.NotificationStatus != NotificationStatus.NotificationSent)
        {
            if (!subscription.CreateTasks) // Only save if we didn't already save above
            {
                await context.SaveChangesAsync(ct);
            }

            // Trigger AI Actor think cycle even if no notification was sent
            await TriggerAiActorIfApplicableAsync(subscriptionId, queryResult.TotalRecords, ct);
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

        await context.SaveChangesAsync(ct);

        // Record anomaly event if anomaly was detected
        if (anomalyEvaluation?.IsAnomaly == true)
        {
            await anomalyDetectionService.RecordAnomalyEventAsync(
                subscriptionId,
                anomalyEvaluation,
                notifications.FirstOrDefault()?.Id,
                ct);
        }

        var recipientsQueryResults = new List<RecipientQueryResult>();
        QueryResultFile? resultFile = null;

        // Only create attachment if subscription has attachments enabled and a file type is specified
        if (subscription.IncludeAttachment && subscription.ResultAttachmentType.HasValue)
        {
            resultFile = await ExportProvider.GetReport(subscription.ResultAttachmentType.Value, queryResult.AllRecords);
        }

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
                await notificationService.SendNotification(recipientQueryResult, lastExecutedQuery?.ResultCount, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send notification for subscription {SubscriptionId}", subscriptionId);
            executedQuery.NotificationStatus = NotificationStatus.Failed;
            executedQuery.Comment = ex.Message;
            await context.SaveChangesAsync(ct);
            throw;
        }

        // Trigger AI Actor think cycle if this subscription belongs to an actor
        await TriggerAiActorIfApplicableAsync(subscriptionId, queryResult.TotalRecords, ct);
    }

    public async Task EvaluateDataContract(int contractId, IJobCancellationToken cancellationToken)
    {
        var ct = cancellationToken.ShutdownToken;

        try
        {
            var evaluationResult = await dataQualityEvaluationService.EvaluateContractAsync(contractId);

            await SendDataQualityNotificationsIfNeeded(contractId, evaluationResult, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to evaluate data contract {ContractId}", contractId);
            throw;
        }
    }

    private async Task SendDataQualityNotificationsIfNeeded(
        int contractId,
        Models.DataQuality.DataQualityEvaluationData evaluationResult,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var contract = await context.DataContracts
            .Where(c => c.Id == contractId)
            .Select(c => new
            {
                c.Name,
                c.AlertOnFailure,
                c.FailureThresholdScore,
                c.SchemaName,
                c.TableName,
                DataSourceName = c.DataSource.Name,
                Recipients = c.Recipients.Select(r => new
                {
                    r.Id,
                    r.Name,
                    r.Destination,
                    r.NotificationType,
                    r.HeadersJson,
                    r.BodyTemplate
                }).ToList()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (contract == null)
        {
            logger.LogWarning("Data contract {ContractId} not found for notification check", contractId);
            return;
        }

        if (!contract.AlertOnFailure)
        {
            logger.LogDebug("AlertOnFailure is disabled for contract {ContractId} '{ContractName}', skipping notifications",
                contractId, contract.Name);
            return;
        }

        if (contract.Recipients.Count == 0)
        {
            logger.LogDebug("No recipients configured for contract {ContractId} '{ContractName}', skipping notifications",
                contractId, contract.Name);
            return;
        }

        if (evaluationResult.OverallScore >= contract.FailureThresholdScore)
        {
            logger.LogDebug("Contract {ContractId} '{ContractName}' score {Score:F1}% is above threshold {Threshold}%, no notification needed",
                contractId, contract.Name, evaluationResult.OverallScore, contract.FailureThresholdScore);
            return;
        }

        logger.LogInformation("Contract {ContractId} '{ContractName}' score {Score:F1}% is below threshold {Threshold}%, sending notifications to {RecipientCount} recipients",
            contractId, contract.Name, evaluationResult.OverallScore, contract.FailureThresholdScore, contract.Recipients.Count);

        // Build a summary of failed rules
        var failedRules = evaluationResult.RuleResults
            .Where(r => !r.Passed)
            .ToList();

        var failedRulesSummary = failedRules
            .Select(r => (IDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["Rule"] = r.RuleName,
                ["Score"] = $"{r.Score:F1}%",
                ["Expected"] = r.ExpectedValue,
                ["Actual"] = r.ActualValue,
                ["Message"] = r.Message
            })
            .ToList();

        var queryResult = new QueryResult
        {
            SubscriptionName = $"[Data Quality] {contract.Name}",
            SubscriptionId = null,
            DataSourceName = contract.DataSourceName,
            TotalRecords = failedRules.Count,
            SqlQuery = string.Empty,
            QueryResults = System.Text.Json.JsonSerializer.Serialize(new
            {
                ContractName = contract.Name,
                OverallScore = $"{evaluationResult.OverallScore:F1}%",
                Threshold = $"{contract.FailureThresholdScore}%",
                Table = $"{contract.SchemaName}.{contract.TableName}",
                PassedRules = evaluationResult.PassedRules,
                FailedRules = evaluationResult.FailedRules,
                TotalRules = evaluationResult.TotalRules,
                FailedRuleDetails = failedRules.Select(r => new { r.RuleName, r.Score, r.Message })
            }),
            TopRecords = failedRulesSummary,
            AllRecords = failedRulesSummary,
            ShowQuery = false
        };

        foreach (var recipient in contract.Recipients)
        {
            var recipientQueryResult = new RecipientQueryResult
            {
                RecipientDestination = recipient.Destination,
                RecipientNotificationType = recipient.NotificationType,
                QueryResult = queryResult,
                HeadersJson = recipient.HeadersJson,
                BodyTemplate = recipient.BodyTemplate
            };

            try
            {
                await notificationService.SendNotification(recipientQueryResult, null, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send data quality notification to recipient {RecipientId} '{RecipientName}' for contract {ContractId}",
                    recipient.Id, recipient.Name, contractId);
            }
        }
    }

    public async Task AggregateLearnedPatterns(IJobCancellationToken cancellationToken)
    {
        if (mcpLearningService == null)
        {
            logger.LogDebug("MCP Learning service not available, skipping aggregation");
            return;
        }

        await mcpLearningService.AggregateLearnedPatternsAsync(cancellationToken.ShutdownToken);
    }

    public async Task GenerateDocumentationPatches(IJobCancellationToken cancellationToken)
    {
        if (mcpLearningService == null)
        {
            logger.LogDebug("MCP Learning service not available, skipping patch generation");
            return;
        }

        await mcpLearningService.GenerateDocumentationPatchesAsync(cancellationToken.ShutdownToken);
    }

    public async Task CleanupOldSignals(IJobCancellationToken cancellationToken)
    {
        if (mcpLearningService == null)
        {
            logger.LogDebug("MCP Learning service not available, skipping signal cleanup");
            return;
        }

        await mcpLearningService.CleanupOldSignalsAsync(ct: cancellationToken.ShutdownToken);
    }

    private async Task TriggerAiActorIfApplicableAsync(int subscriptionId, int rowCount, CancellationToken cancellationToken)
    {
        // AI Actor service is optional - only available if Beacon.AI is added
        if (aiActorService == null)
        {
            return;
        }

        try
        {
            await aiActorService.OnSubscriptionExecutedAsync(subscriptionId, rowCount, cancellationToken);
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
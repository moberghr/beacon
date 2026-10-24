using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Adapters;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Enums;
using Beacon.Core.Exceptions;
using Beacon.Core.Helpers;
using Beacon.Core.Helpers.File;
using Beacon.Core.Models;
using Beacon.Core.Notifications;
using Beacon.Core.Services;

namespace Beacon.Core.Worker.Services;

/// <summary>
/// The work Beacon's jobs run. A subscription's scheduled run and a data contract's scheduled evaluation run as the
/// owner they recorded (<see cref="IRunAsScope"/>), re-authorized at every run right before any SQL: a run the owner may
/// no longer make is skipped (recorded, not failed) and delivers nothing. A run a request asks for (a subscription's test
/// delivery) runs as the request's caller. Maintenance work runs as the system in the job host's marked scope. While the
/// change approval workflow covers recipients, a delivery skips every recipient whose destination waits for approval
/// (<see cref="RecipientDeliveryApproval"/>, checked on the copy the run loaded; the send itself checks it again): no
/// notification is recorded for it, the run's comment names it, and each skip is logged. A run that would notify only
/// recipients that wait notifies nobody: it is recorded as skipped with the remedy, and it does not become the baseline
/// a later run's trigger compares with (no skipped run does). A data-quality alert that every recipient waits for is
/// logged as a Warning and recorded on the evaluation.
/// </summary>
internal class JobService(
    IDbContextFactory<BeaconContext> contextFactory,
    IQueryService queryService,
    INotificationService notificationService,
    ITaskService taskService,
    IAnomalyDetectionService anomalyDetectionService,
    IDataQualityEvaluationService dataQualityEvaluationService,
    ILogger<JobService> logger,
    IRunAsScope runAs,
    RecipientDeliveryApproval deliveryApproval,
    IBeaconRequestPrincipal requestPrincipal,
    IAiActorService? aiActorService = null,
    IMcpLearningAggregationService? mcpLearningService = null,
    IEmbeddingIndexingService? embeddingIndexingService = null,
    IDocChunkIndexingService? docChunkIndexingService = null,
    IMcpEvalService? mcpEvalService = null,
    IOptions<SubscriptionOptions>? subscriptionOptions = null)
    : IJobService
{
    // AI Actor service is optional - only available if Beacon.AI is added

    public async Task ExecuteQuery(int subscriptionId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var subscription = await context.Subscriptions
            .Where(x => x.Id == subscriptionId)
            .FirstOrDefaultAsync(cancellationToken);

        if (subscription == null)
        {
            return;
        }

        // A scheduled run acts as the subscription's owner as they are now; a run a request asks for acts as its caller.
        var isScheduledRun = !requestPrincipal.HasRequest;
        if (isScheduledRun)
        {
            runAs.RunAs(subscription.OwnerUserId);
        }

        QueryResult queryResult;
        try
        {
            queryResult = await queryService.ExecuteQuery(subscriptionId, cancellationToken);
        }
        catch (ExecutionRefusedException ex) when (isScheduledRun)
        {
            await RecordRunNotMadeAsync(context, subscriptionId, ex.DataSourceIds, NotificationStatus.Skipped, NotificationFailureReasons.OwnerNotAuthorized, cancellationToken);

            return;
        }
        catch (DataSourceDisabledException ex)
        {
            // Nothing ran: recorded as failed with a generic reason (never the exception's text), with the data sources the
            // run would have read. A run a request asks for then answers 409.
            await RecordRunNotMadeAsync(context, subscriptionId, ex.DataSourceIds, NotificationStatus.Failed, NotificationFailureReasons.DataSourceDisabled, cancellationToken);
            if (isScheduledRun)
            {
                return;
            }

            throw new BeaconConflictException(NotificationFailureReasons.DataSourceDisabled);
        }
        catch (DataSourceAwaitingApprovalException ex)
        {
            // Nothing ran (or nothing more did): the connection of a source the run reads waits for approval. Recorded as
            // failed with a generic reason and the data sources the run would have read; a run a request asks for answers 409.
            await RecordRunNotMadeAsync(context, subscriptionId, ex.DataSourceIds, NotificationStatus.Failed, NotificationFailureReasons.DataSourceAwaitingApproval, cancellationToken);
            if (isScheduledRun)
            {
                return;
            }

            throw new BeaconConflictException(NotificationFailureReasons.DataSourceAwaitingApproval);
        }
        catch (QueryNotReviewedException ex)
        {
            // With the approval workflow on, the stored SQL is not the reviewed version: nothing ran. Recorded for a run a
            // request asks for too, which then answers 409.
            await RecordRunNotMadeAsync(context, subscriptionId, ex.DataSourceIds, NotificationStatus.Skipped, NotificationFailureReasons.NotApproved, cancellationToken);
            if (isScheduledRun)
            {
                return;
            }

            throw new BeaconConflictException(ex.Message);
        }

        // Set subscription specific parameters. MaxRows stays the effective cap the query service applied (the
        // subscription's own, else the default); the subscription's nullable value would erase the default.
        queryResult.ShowQuery = subscription.ShowQuery;

        // Apply max rows limit if specified
        if (subscription.MaxRows.HasValue && subscription.MaxRows > 0)
        {
            queryResult.AllRecords = queryResult.AllRecords.Take(subscription.MaxRows.Value).ToList();
            queryResult.TopRecords = queryResult.TopRecords.Take(subscription.MaxRows.Value).ToList();
        }

        // A skipped run (nothing ran, or nobody was notified) is no baseline for the trigger.
        var lastExecutedQuery = await context.QueryExecutionHistory
            .Where(x => x.SubscriptionId == subscriptionId)
            .Where(x => x.NotificationStatus != NotificationStatus.Skipped)
            .OrderByDescending(x => x.CreatedTime)
            .Select(x =>
                new
                {
                    x.ResultCount
                })
            .FirstOrDefaultAsync(cancellationToken);

        // Check if anomaly detection is enabled for this subscription
        var hasAnomalyDetection = await context.AnomalyConfigs
            .Where(x => x.SubscriptionId == subscriptionId)
            .Where(x => x.Enabled)
            .AnyAsync(cancellationToken);

        Models.Anomaly.AnomalyEvaluationResult? anomalyEvaluation = null;

        // Only evaluate anomaly detection if it's enabled
        if (hasAnomalyDetection)
        {
            anomalyEvaluation = await anomalyDetectionService.EvaluateAnomalyAsync(
                subscriptionId,
                queryResult.TotalRecords,
                cancellationToken);
        }

        var status = DetermineNotificationStatus(
            queryResult,
            subscription,
            lastExecutedQuery?.ResultCount,
            hasAnomalyDetection,
            anomalyEvaluation,
            subscriptionId);

        // Whom a delivery reaches, decided before the run's outcome: a recipient whose destination waits for approval
        // receives nothing, and a run that would notify only such recipients notifies nobody.
        var held = status == NotificationStatus.NotificationSent
            ? await deliveryApproval.HeldCopiesAsync(CopiesOf(queryResult.Recipients), cancellationToken)
            : new HashSet<int>();
        var recipients = queryResult.Recipients
            .Where(x => x.RecipientId is not { } id || !held.Contains(id))
            .ToList();
        var nobodyNotified = held.Count > 0 && recipients.Count == 0;
        LogHeld(held, "subscription", subscriptionId);
        if (nobodyNotified)
        {
            status = NotificationStatus.Skipped;
            logger.LogWarning(
                "Subscription {SubscriptionId} notified nobody: each of its {RecipientCount} recipients waits for approval of where it delivers",
                subscriptionId,
                held.Count);
        }

        // Store baseline for future anomaly detection, unless nobody could be told about this run.
        if (hasAnomalyDetection && !nobodyNotified)
        {
            await anomalyDetectionService.StoreBaselineAsync(
                subscriptionId,
                queryResult.TotalRecords,
                DateTime.UtcNow,
                cancellationToken);
        }

        var executedQuery = new QueryExecutionHistory
        {
            SubscriptionId = subscriptionId,
            ResultCount = queryResult.TotalRecords,
            CompiledSql = queryResult.SqlQuery,
            NotificationStatus = status,
            Comment = nobodyNotified ? NotificationFailureReasons.AllRecipientsAwaitApproval : HeldComment(held),
            ExecutionTimeMs = queryResult.ExecutionTimeMs,
            Results = subscription.StoreResults ? queryResult.QueryResults : null,
            // Which projects' readers may see this run (StoredRunAccess) is decided by what it read.
            DataSourceIds = queryResult.DataSourceIds
                .Distinct()
                .Order()
                .ToArray()
        };

        await context.QueryExecutionHistory.AddAsync(executedQuery, cancellationToken);

        // Handle tasks for subscriptions with CreateTasks enabled (even if no notifications to send)
        // This runs regardless of NotificationStatus to handle auto-resolve on 0 results
        if (subscription.CreateTasks)
        {
            await context.SaveChangesAsync(cancellationToken); // Save QueryExecutionHistory first

            logger.LogDebug("Creating/updating task for subscription {SubscriptionId}, result count {ResultCount}",
                subscriptionId, queryResult.TotalRecords);
            await taskService.CreateOrUpdateTask(
                subscriptionId,
                queryResult.TotalRecords,
                cancellationToken
            );
        }

        // Only send notification if the status is NotificationSent
        if (executedQuery.NotificationStatus != NotificationStatus.NotificationSent)
        {
            if (!subscription.CreateTasks) // Only save if we didn't already save above
            {
                await context.SaveChangesAsync(cancellationToken);
            }

            // Trigger AI Actor think cycle even if no notification was sent
            await TriggerAiActorIfApplicableAsync(subscriptionId, queryResult.TotalRecords, cancellationToken);

            // A delivery a request asked for that reached nobody answers why (the run is recorded).
            if (nobodyNotified && !isScheduledRun)
            {
                throw new BeaconConflictException(NotificationFailureReasons.AllRecipientsAwaitApproval);
            }

            return;
        }

        QueryResultFile? resultFile = null;

        // Only create attachment if subscription has attachments enabled and a file type is specified. Built before any
        // Notification row exists, so a failure here leaves no recipient recorded as notified.
        if (subscription.IncludeAttachment && subscription.ResultAttachmentType.HasValue)
        {
            // Within Beacon:Subscriptions:MaxAttachmentBytes: a larger export keeps its first rows that fit, and the message
            // says so.
            var maxAttachmentBytes = (subscriptionOptions?.Value ?? new SubscriptionOptions()).MaxAttachmentBytes;
            resultFile = await ExportProvider.GetReport(subscription.ResultAttachmentType.Value, queryResult.AllRecords, maxAttachmentBytes, cancellationToken);
            if (resultFile.Omitted)
            {
                logger.LogWarning(
                    "Subscription {SubscriptionId} attachment omitted: its column headers alone exceed {MaxAttachmentBytes} bytes",
                    subscriptionId,
                    maxAttachmentBytes);
            }
            else if (resultFile.Truncated)
            {
                logger.LogWarning(
                    "Subscription {SubscriptionId} attachment truncated to {RowCount} of {SourceRowCount} rows to stay within {MaxAttachmentBytes} bytes",
                    subscriptionId,
                    resultFile.RowCount,
                    resultFile.SourceRowCount,
                    maxAttachmentBytes);
            }
        }

        // One Notification row per recipient, saved just before sending because its id goes into the message (the
        // link back to Beacon). A row whose delivery fails is removed as soon as the sends are done, before any other
        // write, so a Notification always means the recipient was notified.
        var notifications = new List<Notification>();
        foreach (var recipient in recipients)
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

        await context.SaveChangesAsync(cancellationToken);

        // Every recipient is tried; one failing does not stop the others.
        var failures = new List<(int? RecipientId, string Reason)>();
        var delivered = new List<Notification>();
        for (var i = 0; i < recipients.Count; i++)
        {
            var recipient = recipients[i];
            var recipientQueryResult = new RecipientQueryResult
            {
                RecipientDestination = recipient.Destination,
                RecipientNotificationType = recipient.NotificationType,
                QueryResult = queryResult,
                QueryResultFile = resultFile,
                NotificationId = notifications[i].Id,
                RecipientId = recipient.RecipientId,
                AnomalyEvaluation = anomalyEvaluation?.IsAnomaly == true ? anomalyEvaluation : null,
                HeadersJson = recipient.HeadersJson,
                BodyTemplate = recipient.BodyTemplate
            };

            try
            {
                await notificationService.SendNotification(recipientQueryResult, lastExecutedQuery?.ResultCount, cancellationToken);
                delivered.Add(notifications[i]);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutting down: the recipients not reached yet were not notified either.
                failures.AddRange(recipients
                    .Skip(i)
                    .Select(x => (x.RecipientId, NotificationFailureReasons.Generic)));
                await RecordFailedDeliveriesAsync(context, executedQuery, notifications.Except(delivered), failures, CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                failures.Add((recipient.RecipientId, NotificationFailureReasons.For(ex)));
            }
        }

        // The outcome is persisted before any other write, and with a token the caller's cancellation cannot stop: a
        // failing anomaly write or a cancelled request must not leave failed recipients recorded as notified.
        if (failures.Count > 0)
        {
            await RecordFailedDeliveriesAsync(context, executedQuery, notifications.Except(delivered), failures, CancellationToken.None);
        }

        // Record anomaly event if anomaly was detected, linked to a notification that actually went out
        if (anomalyEvaluation?.IsAnomaly == true)
        {
            await anomalyDetectionService.RecordAnomalyEventAsync(
                subscriptionId,
                anomalyEvaluation,
                delivered.FirstOrDefault()?.Id,
                cancellationToken);
        }

        if (failures.Count > 0)
        {
            logger.LogError(
                "Notification delivery failed for {FailedCount} of {RecipientCount} recipients of subscription {SubscriptionId}",
                failures.Count,
                notifications.Count,
                subscriptionId);

            throw new NotificationDeliveryException(executedQuery.Comment!);
        }

        // Trigger AI Actor think cycle if this subscription belongs to an actor
        await TriggerAiActorIfApplicableAsync(subscriptionId, queryResult.TotalRecords, cancellationToken);
    }

    public async Task EvaluateDataContract(int contractId, CancellationToken cancellationToken)
    {
        // A scheduled evaluation acts as the contract's owner as they are now.
        var isScheduledRun = !requestPrincipal.HasRequest;
        if (isScheduledRun)
        {
            await RunAsContractOwnerAsync(contractId, cancellationToken);
        }

        try
        {
            var evaluationResult = await dataQualityEvaluationService.EvaluateContractAsync(contractId, cancellationToken);

            await SendDataQualityNotificationsIfNeeded(contractId, evaluationResult, cancellationToken);
        }
        catch (DataContractHasNoEnabledRulesException)
        {
            // Nothing to score: no history, score or alert is written, and the recurring job is not failed for it.
            logger.LogWarning("Data contract {ContractId} has no enabled rules; its scheduled evaluation was skipped", contractId);
        }
        catch (DataContractRulesAwaitApprovalException ex)
        {
            // Nothing to score until a rule is approved: as above, nothing is written and the job is not failed for it.
            logger.LogWarning(
                "Data contract {ContractId} was not evaluated: each of its {AwaitingCount} enabled rules is a Custom SQL rule waiting for approval",
                contractId,
                ex.AwaitingRules);
        }
        catch (ExecutionRefusedException) when (isScheduledRun)
        {
            // The owner may no longer run the contract's rules: logged as skipped work and recorded on the contract, so
            // its readers see it; nothing is scored or alerted, and the recurring job is not failed for it.
            await RecordSkippedEvaluationAsync(contractId, cancellationToken);
        }
        catch (Exception ex)
        {
            // The type and error code only (§1.11): a rule's failure can quote its SQL or row values.
            logger.LogError(
                "Failed to evaluate data contract {ContractId}: {Failure}",
                contractId,
                FailureCodes.Describe(ex));
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
            if (evaluationResult.AwaitingApprovalRules > 0)
            {
                // A partial score: the rules waiting for approval were not evaluated, so it says nothing about them.
                logger.LogWarning(
                    "Contract {ContractId} scored {Score:F1}% without its {AwaitingCount} Custom SQL rules that wait for approval; no alert is sent for a partial score above the threshold",
                    contractId,
                    evaluationResult.OverallScore,
                    evaluationResult.AwaitingApprovalRules);

                return;
            }

            logger.LogDebug("Contract {ContractId} '{ContractName}' score {Score:F1}% is above threshold {Threshold}%, no notification needed",
                contractId, contract.Name, evaluationResult.OverallScore, contract.FailureThresholdScore);
            return;
        }

        // A recipient whose destination waits for approval receives nothing; the evaluation records how many were held.
        var held = await deliveryApproval.HeldCopiesAsync(
            contract.Recipients
                .Select(x => new RecipientCopy(x.Id, x.NotificationType, x.Destination, x.HeadersJson))
                .ToList(),
            cancellationToken);
        var recipients = contract.Recipients
            .Where(x => !held.Contains(x.Id))
            .ToList();
        LogHeld(held, "data contract", contractId);
        if (held.Count > 0)
        {
            await context.DataQualityEvaluations
                .Where(x => x.Id == evaluationResult.Id)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.HeldAlertRecipients, held.Count), cancellationToken);
        }

        if (recipients.Count == 0)
        {
            logger.LogWarning(
                "The alert of data contract {ContractId} (score {Score:F1}% below its threshold) reached nobody: each of its {RecipientCount} recipients waits for approval of where it delivers",
                contractId,
                evaluationResult.OverallScore,
                held.Count);
            return;
        }

        logger.LogInformation("Contract {ContractId} '{ContractName}' score {Score:F1}% is below threshold {Threshold}%, sending notifications to {RecipientCount} recipients",
            contractId, contract.Name, evaluationResult.OverallScore, contract.FailureThresholdScore, recipients.Count);

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
                AwaitingApprovalRules = evaluationResult.AwaitingApprovalRules,
                FailedRuleDetails = failedRules.Select(r => new { r.RuleName, r.Score, r.Message })
            }),
            TopRecords = failedRulesSummary,
            AllRecords = failedRulesSummary,
            ShowQuery = false
        };

        foreach (var recipient in recipients)
        {
            var recipientQueryResult = new RecipientQueryResult
            {
                RecipientDestination = recipient.Destination,
                RecipientNotificationType = recipient.NotificationType,
                QueryResult = queryResult,
                RecipientId = recipient.Id,
                HeadersJson = recipient.HeadersJson,
                BodyTemplate = recipient.BodyTemplate
            };

            try
            {
                await notificationService.SendNotification(recipientQueryResult, null, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The reason and the exception types only: a host's notification service may put remote text in messages.
                logger.LogError(
                    "Data quality notification to recipient {RecipientId} for contract {ContractId} failed: {Reason} ({ExceptionTypes})",
                    recipient.Id,
                    contractId,
                    NotificationFailureReasons.For(ex),
                    NotificationHttpClient.TypeChain(ex));
            }
        }
    }

    public async Task AggregateLearnedPatterns(CancellationToken cancellationToken)
    {
        if (mcpLearningService == null)
        {
            logger.LogDebug("MCP Learning service not available, skipping aggregation");
            return;
        }

        await mcpLearningService.AggregateLearnedPatternsAsync(cancellationToken);
    }

    public async Task CleanupOldSignals(CancellationToken cancellationToken)
    {
        if (mcpLearningService == null)
        {
            logger.LogDebug("MCP Learning service not available, skipping signal cleanup");
            return;
        }

        // 0: each project's own retention window, the global window for signals without a project.
        await mcpLearningService.CleanupOldSignalsAsync(retentionDays: 0, ct: cancellationToken);
    }

    public async Task ReindexEmbeddings(CancellationToken cancellationToken)
    {
        if (embeddingIndexingService == null)
        {
            logger.LogDebug("Embedding indexing service not available, skipping re-index");
            return;
        }

        await embeddingIndexingService.ReindexAsync(cancellationToken);
    }

    public async Task ReindexDocChunks(CancellationToken cancellationToken)
    {
        if (docChunkIndexingService == null)
        {
            logger.LogDebug("Doc-chunk indexing service not available, skipping re-index");
            return;
        }

        await docChunkIndexingService.ReindexAsync(cancellationToken);
    }

    public async Task RunMcpEval(int runId, CancellationToken cancellationToken)
    {
        if (mcpEvalService == null)
        {
            logger.LogDebug("MCP eval service not available, skipping eval run {RunId}", runId);
            return;
        }

        await mcpEvalService.RunAsync(runId, cancellationToken);
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
            // Log but don't fail the subscription execution: the type and error code only (§1.11).
            logger.LogWarning(
                "Failed to trigger AI Actor for subscription {SubscriptionId}: {Failure}",
                subscriptionId,
                FailureCodes.Describe(ex));
        }
    }

    private async Task RunAsContractOwnerAsync(int contractId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var contract = await context.DataContracts
            .Where(x => x.Id == contractId)
            .Select(x =>
                new
                {
                    x.OwnerUserId
                })
            .FirstOrDefaultAsync(cancellationToken);

        // A missing contract fails its evaluation as before; there is no owner to run as.
        if (contract != null)
        {
            runAs.RunAs(contract.OwnerUserId);
        }
    }

    // A scheduled evaluation its owner may no longer run: the contract records since when (the first skip of a series is
    // kept), until an evaluation runs again.
    private async Task RecordSkippedEvaluationAsync(int contractId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        await context.DataContracts
            .Where(x => x.Id == contractId)
            .Where(x => x.OwnerNotAuthorizedSince == null)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.OwnerNotAuthorizedSince, DateTime.UtcNow), cancellationToken);
    }

    // A run that may not happen (a scheduled run its owner may no longer make, a run of SQL the approval workflow has not
    // approved: skipped; a run on a disabled data source: failed): recorded with a neutral reason, with the data sources it
    // would have read so the readers of the subscription see it, and nothing else happens (no task, no baseline, no
    // delivery, no AI actor).
    private static async Task RecordRunNotMadeAsync(
        BeaconContext context,
        int subscriptionId,
        IReadOnlyList<int> dataSourceIds,
        NotificationStatus status,
        string reason,
        CancellationToken cancellationToken)
    {
        var run = new QueryExecutionHistory
        {
            SubscriptionId = subscriptionId,
            ResultCount = 0,
            CompiledSql = string.Empty,
            NotificationStatus = status,
            ExecutionTimeMs = 0,
            Comment = reason,
            DataSourceIds = dataSourceIds
                .Distinct()
                .Order()
                .ToArray()
        };

        await context.QueryExecutionHistory.AddAsync(run, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    // The run failed if any recipient was not notified: those recipients' Notification rows are removed, and the
    // comment, shown to every reader of the history, names them with a generic reason each (never remote content).
    private static async Task RecordFailedDeliveriesAsync(
        BeaconContext context,
        QueryExecutionHistory executedQuery,
        IEnumerable<Notification> undelivered,
        List<(int? RecipientId, string Reason)> failures,
        CancellationToken cancellationToken)
    {
        foreach (var notification in undelivered)
        {
            executedQuery.Notifications.Remove(notification);
            if (context.Entry(notification).State != EntityState.Detached)
            {
                context.Remove(notification);
            }
        }

        // The recipients skipped as waiting for approval stay named, before the failures.
        executedQuery.NotificationStatus = NotificationStatus.Failed;
        executedQuery.Comment = string.Join(
            " ",
            new[] { executedQuery.Comment }
                .Concat(failures.Select(x => $"Recipient {x.RecipientId}: {x.Reason}"))
                .Where(x => !string.IsNullOrEmpty(x)));
        await context.SaveChangesAsync(cancellationToken);
    }

    // What a run loaded of its stored recipients, as the delivery check reads it; an address no stored recipient holds is
    // not a governed recipient's.
    private static List<RecipientCopy> CopiesOf(IEnumerable<Models.Recipients.RecipientData> recipients)
    {
        return recipients
            .Where(x => x.RecipientId.HasValue)
            .Select(x => new RecipientCopy(x.RecipientId!.Value, x.NotificationType, x.Destination, x.HeadersJson))
            .ToList();
    }

    // Each recipient a delivery skipped because where it delivers waits for approval, logged by id only.
    private void LogHeld(IEnumerable<int> held, string work, int workId)
    {
        foreach (var recipientId in held)
        {
            logger.LogWarning(
                "Recipient {RecipientId} of {Work} {WorkId} was skipped: where it delivers waits for approval",
                recipientId,
                work,
                workId);
        }
    }

    // The run's comment naming each recipient it skipped because where it delivers waits for approval; null for none.
    private static string? HeldComment(IReadOnlySet<int> held)
    {
        return held.Count == 0
            ? null
            : string.Join(" ", held.Order().Select(x => $"Recipient {x}: {NotificationFailureReasons.AwaitingApproval}"));
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
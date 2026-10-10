using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Adapters;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models.QueryExecutionHistory;
using Beacon.Core.Notifications;

namespace Beacon.Core.Services;

internal class NotificationService(
    IDbContextFactory<BeaconContext> contextFactory,
    AdapterFactory adapterFactory,
    RecipientSecretProtector secretProtector,
    NotificationDestinationPolicy destinationPolicy,
    IOptions<NotificationChannelOptions> options,
    ILogger<NotificationService> logger) : INotificationService
{
    private static int _plaintextWarningLogged;

    /// <summary>
    /// The single path to every notification adapter. The stored destination and headers are decrypted here and checked
    /// against the destination policy again, so a recipient saved before the policy (or under a looser configuration)
    /// is never sent to. Every failure is logged at Warning (type, notification and recipient ids, reason, HTTP status,
    /// exception types; never a message, URL or body) and leaves as a <see cref="NotificationDeliveryException"/> whose
    /// message is a generic reason.
    /// </summary>
    public async Task SendNotification(
        RecipientQueryResult recipientQueryResult,
        int? lastExecutedQueryResultCount,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolved = ResolveDestination(recipientQueryResult);

            // Task creation is handled in JobService.ExecuteQuery; here we fan out to the
            // configured notification adapter (Email/Slack/Teams/Jira/Webhook). The token
            // flows so a shutdown signal aborts an in-flight HTTP call rather than letting
            // a slow third-party hang the worker.
            var adapter = adapterFactory.GetAdapterService(recipientQueryResult.RecipientNotificationType);
            await adapter.SendNotificationAsync(resolved, lastExecutedQueryResultCount, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var reason = NotificationFailureReasons.For(ex);
            var statusCode = NotificationFailureReasons.StatusOf(ex);
            logger.LogWarning(
                "{NotificationType} notification {NotificationId} to recipient {RecipientId} failed: {Reason} (HTTP {StatusCode}; {ExceptionTypes})",
                recipientQueryResult.RecipientNotificationType,
                recipientQueryResult.NotificationId,
                recipientQueryResult.RecipientId,
                reason,
                (int?)statusCode,
                NotificationHttpClient.TypeChain(ex));

            if (ex is NotificationDeliveryException)
            {
                throw;
            }

            throw new NotificationDeliveryException(reason, statusCode);
        }
    }

    public async Task<PagedList<QueryExecutionHistoryData>> GetQueryExecutionHistory(GetQueryExecutionHistoryRequest request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var page = await context.QueryExecutionHistory
            .WhereIf(request.SubscriptionId.HasValue, x => x.SubscriptionId == request.SubscriptionId)
            .WhereIf(request.NotificationStatus.HasValue, x => x.NotificationStatus == request.NotificationStatus)
            .WhereReadableWithin(context, request.Scope)
            .Select(x => new QueryExecutionHistoryData
            {
                QueryExecutionHistoryId = x.Id,
                Notifications = x.Notifications.Select(y => new NotificationData
                {
                    Id = y.Id,
                    Created = y.CreatedTime,
                    NotificationType = y.Type,
                    RecipientName = y.Recipient.Name,
                    SentAt = y.SentAt
                }).ToList(),
                ResultCount = x.ResultCount,
                CreatedTime = x.CreatedTime,
                NotificationStatus = x.NotificationStatus,
                QueryName = x.Subscription.Query.Name,
                SubscriptionId = x.SubscriptionId,
                ExecutionTimeMs = x.ExecutionTimeMs,
                Comment = x.Comment,
                AiActorId = x.Subscription.AiActorId,
                AiActorName = x.Subscription.AiActor != null ? x.Subscription.AiActor.Name : null
            })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-createdTime", tiebreaker: "queryExecutionHistoryId");

        // A run's stored comment can hold failure detail recorded by earlier versions: only a recorded reason leaves.
        foreach (var item in page.Items)
        {
            item.Comment = NotificationFailureReasons.Displayable(item.Comment);
        }

        return page;
    }

    public async Task<NotificationStatisticsData> GetNotificationStatistics(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var cutoffDate = DateTime.UtcNow.AddDays(-30);

        // Get query execution statistics
        var queryStats = await context.QueryExecutionHistory
            .Where(x => x.CreatedTime >= cutoffDate)
            .GroupBy(x => x.CreatedTime.Date)
            .Select(x => new
            {
                Date = x.Key,
                TotalQueries = x.Count(),
                NotificationsSent = x.Count(y => y.NotificationStatus == NotificationStatus.NotificationSent)
            })
            .ToListAsync(cancellationToken);

        // Get migration execution statistics
        var migrationStats = await context.MigrationExecutions
            .Where(x => x.StartedAt >= cutoffDate)
            .GroupBy(x => x.StartedAt.Date)
            .Select(x => new
            {
                Date = x.Key,
                MigrationExecutions = x.Count(),
                SuccessfulMigrationExecutions = x.Count(m => m.Status == MigrationStatus.Completed)
            })
            .ToListAsync(cancellationToken);

        // Merge the data by date
        var allDates = queryStats.Select(x => x.Date)
            .Union(migrationStats.Select(x => x.Date))
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        var dates = allDates.Select(date => new NotificationDateStatisticsData
        {
            Date = date,
            TotalQueries = queryStats.FirstOrDefault(x => x.Date == date)?.TotalQueries ?? 0,
            NotificationsSent = queryStats.FirstOrDefault(x => x.Date == date)?.NotificationsSent ?? 0,
            MigrationExecutions = migrationStats.FirstOrDefault(x => x.Date == date)?.MigrationExecutions ?? 0,
            SuccessfulMigrationExecutions = migrationStats.FirstOrDefault(x => x.Date == date)?.SuccessfulMigrationExecutions ?? 0
        }).ToList();

        return new NotificationStatisticsData
        {
            NotificationDateStatistics = dates
        };
    }

    public async Task<NotificationDetailsData?> GetNotificationDetails(
        int queryExecutionHistoryId,
        StoredRunScope scope,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The notifications list exposes one row per QueryExecutionHistory (aggregating
        // its notifications by recipient). Clicking through passes that history id, so
        // resolve the detail by history id and surface its first notification's recipient
        // / type — matches the single-recipient detail UI on the React side.
        //
        // Two-step load: scalar subqueries over an empty Notifications collection caused
        // EF/Npgsql to materialize the entire projected row as null, surfacing as a 404
        // for histories that have no notifications yet.
        var details = await context.QueryExecutionHistory
            .Where(x => x.Id == queryExecutionHistoryId)
            .WhereReadableWithin(context, scope)
            .Select(x =>
                new NotificationDetailsData
                {
                    Id = x.Id,
                    CreatedTime = x.CreatedTime,
                    SentAt = x.CreatedTime,
                    Type = default,
                    Results = x.Results,
                    RecipientName = string.Empty,
                    QueryName = x.Subscription.Query.Name,
                    QueryId = x.Subscription.QueryId,
                    SubscriptionId = x.SubscriptionId,
                    ExecutionTimeMs = x.ExecutionTimeMs,
                    ResultCount = x.ResultCount,
                    NotificationStatus = x.NotificationStatus
                })
            .FirstOrDefaultAsync(cancellationToken);

        if (details == null)
        {
            return null;
        }

        var firstNotification = await context.Notifications
            .Where(x => x.QueryExecutionHistoryId == queryExecutionHistoryId)
            .OrderBy(x => x.Id)
            .Select(x =>
                new
                {
                    x.SentAt,
                    x.Type,
                    x.Results,
                    RecipientName = x.Recipient.Name
                })
            .FirstOrDefaultAsync(cancellationToken);

        if (firstNotification != null)
        {
            details.SentAt = firstNotification.SentAt;
            details.Type = firstNotification.Type;
            details.Results = details.Results ?? firstNotification.Results;
            details.RecipientName = firstNotification.RecipientName ?? string.Empty;
        }

        return details;
    }

    private RecipientQueryResult ResolveDestination(RecipientQueryResult recipientQueryResult)
    {
        var storedInPlaintext = !RecipientSecretProtector.IsProtected(recipientQueryResult.RecipientDestination)
            || (!string.IsNullOrEmpty(recipientQueryResult.HeadersJson) && !RecipientSecretProtector.IsProtected(recipientQueryResult.HeadersJson));

        if (storedInPlaintext && options.Value.RequireEncryptedSecrets)
        {
            throw new NotificationDeliveryException(NotificationFailureReasons.DestinationNotEncrypted);
        }

        if (storedInPlaintext)
        {
            WarnOnceStoredInPlaintext();
        }

        string destination;
        string? headersJson;
        try
        {
            destination = secretProtector.Unprotect(recipientQueryResult.RecipientDestination);
            headersJson = secretProtector.UnprotectOptional(recipientQueryResult.HeadersJson);
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
        {
            throw new NotificationDeliveryException(NotificationFailureReasons.DestinationUnreadable);
        }

        try
        {
            destination = destinationPolicy.EnsureAllowed(recipientQueryResult.RecipientNotificationType, destination);
            destinationPolicy.ParseHeaders(headersJson);
        }
        catch (InvalidOperationException)
        {
            throw new NotificationDeliveryException(NotificationFailureReasons.DestinationNotAllowed);
        }

        return new RecipientQueryResult
        {
            RecipientDestination = destination,
            RecipientNotificationType = recipientQueryResult.RecipientNotificationType,
            QueryResult = recipientQueryResult.QueryResult,
            QueryResultFile = recipientQueryResult.QueryResultFile,
            NotificationId = recipientQueryResult.NotificationId,
            RecipientId = recipientQueryResult.RecipientId,
            AnomalyEvaluation = recipientQueryResult.AnomalyEvaluation,
            HeadersJson = headersJson,
            BodyTemplate = recipientQueryResult.BodyTemplate,
        };
    }

    // A recipient saved before secrets were encrypted at rest still works (unless RequireEncryptedSecrets is on), but
    // stays readable in the database until the host runs IRecipientSecretEncryptionService or the recipient is saved
    // again. Logged once per process, no data.
    private void WarnOnceStoredInPlaintext()
    {
        if (Interlocked.Exchange(ref _plaintextWarningLogged, 1) == 0)
        {
            logger.LogWarning(
                "Notification recipient secrets are stored unencrypted; run IRecipientSecretEncryptionService.EncryptStoredSecretsAsync to encrypt them");
        }
    }
}

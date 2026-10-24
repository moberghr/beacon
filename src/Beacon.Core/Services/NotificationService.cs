using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Adapters;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Notifications;
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
    RecipientDeliveryApproval deliveryApproval,
    ILogger<NotificationService> logger) : INotificationService
{
    private static int _plaintextWarningLogged;

    /// <summary>
    /// The single path to every notification adapter. The stored destination and headers are decrypted here and checked
    /// against the destination policy again, so a recipient saved before the policy (or under a looser configuration)
    /// is never sent to. While the change approval workflow covers recipients, a delivery to a stored recipient
    /// (<see cref="RecipientQueryResult.RecipientId"/>) sends only the destination a second person approved: the very copy
    /// being sent is checked against the recipient's approved hash as it is now (archived included), and a recipient
    /// that no longer exists receives nothing (<see cref="NotificationFailureReasons.AwaitingApproval"/>). A delivery to
    /// an address no stored recipient holds (no id) is not a governed recipient's. Every failure is logged at Warning
    /// (type, notification and recipient ids, reason, HTTP status, exception types; never a message, URL or body) and
    /// leaves as a <see cref="NotificationDeliveryException"/> whose message is a generic reason.
    /// </summary>
    public async Task SendNotification(
        RecipientQueryResult recipientQueryResult,
        int? lastExecutedQueryResultCount,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolved = await ResolveDestinationAsync(recipientQueryResult, cancellationToken);

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

    // The two reads live in their handlers now; these forwarders keep the 5.0 surface for one release.
    [Obsolete("Use GetNotificationsQuery; removed in 6.0.")]
    public Task<PagedList<QueryExecutionHistoryData>> GetQueryExecutionHistory(GetQueryExecutionHistoryRequest request, CancellationToken cancellationToken)
    {
        return new GetNotificationsHandler(contextFactory, null!).Read(request, cancellationToken);
    }

    [Obsolete("Use GetNotificationDetailQuery; removed in 6.0.")]
    public Task<NotificationDetailsData?> GetNotificationDetails(int queryExecutionHistoryId, StoredRunScope scope, CancellationToken cancellationToken)
    {
        return new GetNotificationDetailHandler(contextFactory, null!).Read(queryExecutionHistoryId, scope, cancellationToken);
    }

    private async Task<RecipientQueryResult> ResolveDestinationAsync(RecipientQueryResult recipientQueryResult, CancellationToken cancellationToken)
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

        // What is sent is what was approved: this copy, decrypted, against the recipient's approved hash read now.
        if (recipientQueryResult.RecipientId is { } recipientId
            && !await deliveryApproval.AllowsAsync(recipientId, recipientQueryResult.RecipientNotificationType, destination, headersJson, cancellationToken))
        {
            throw new NotificationDeliveryException(NotificationFailureReasons.AwaitingApproval);
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

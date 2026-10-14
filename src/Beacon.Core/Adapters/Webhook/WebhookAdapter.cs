using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Enums;
using Beacon.Core.Notifications;

namespace Beacon.Core.Adapters.Webhook;

internal class WebhookAdapter(
    NotificationHttpSender sender,
    NotificationDestinationPolicy destinationPolicy,
    BeaconConfiguration configuration,
    ILogger<WebhookAdapter> logger) : IAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public NotificationType NotificationType => NotificationType.Webhook;

    public async Task SendNotificationAsync(
        RecipientQueryResult recipientQueryResult,
        int? lastNotificationResultCount,
        CancellationToken cancellationToken = default)
    {
        string bodyContent;
        if (!string.IsNullOrWhiteSpace(recipientQueryResult.BodyTemplate))
        {
            try
            {
                var templateData = Shared.TemplateDataBuilder.BuildTemplateData(recipientQueryResult, lastNotificationResultCount, configuration.BaseUrl);
                bodyContent = Shared.AdapterTemplateProcessor.ProcessTemplate(recipientQueryResult.BodyTemplate, templateData);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process webhook body template, falling back to default payload");
                bodyContent = BuildDefaultPayload(recipientQueryResult, lastNotificationResultCount);
            }
        }
        else
        {
            bodyContent = BuildDefaultPayload(recipientQueryResult, lastNotificationResultCount);
        }

        var content = new StringContent(bodyContent, Encoding.UTF8, System.Net.Mime.MediaTypeNames.Application.Json);

        // Checked against the header allow-list when saved and again here (NotificationService already refused a
        // recipient whose stored headers fail it).
        var headers = destinationPolicy.ParseHeaders(recipientQueryResult.HeadersJson);

        await sender.PostAsync(NotificationType.Webhook, recipientQueryResult.RecipientDestination, content, headers, cancellationToken);
    }

    private string BuildDefaultPayload(RecipientQueryResult recipientQueryResult, int? lastNotificationResultCount)
    {
        var queryResult = recipientQueryResult.QueryResult;

        var payload = new WebhookPayload
        {
            Timestamp = DateTime.UtcNow,
            SubscriptionName = queryResult.SubscriptionName,
            SubscriptionId = queryResult.SubscriptionId,
            DataSourceName = queryResult.DataSourceName,
            SqlQuery = queryResult.ShowQuery ? queryResult.SqlQuery : null!,
            TotalRecords = queryResult.TotalRecords,
            ExecutionTimeMs = queryResult.ExecutionTimeMs,
            PreviousResultCount = lastNotificationResultCount,
            TimedOut = queryResult.TimedOut,
            Records = queryResult.TopRecords,
            NotificationUrl = !string.IsNullOrEmpty(configuration.BaseUrl) && recipientQueryResult.NotificationId.HasValue
                ? $"{configuration.BaseUrl.TrimEnd('/')}/notifications/{recipientQueryResult.NotificationId}"
                : null
        };

        if (recipientQueryResult.AnomalyEvaluation != null)
        {
            payload.Anomaly = new WebhookAnomalyData
            {
                IsAnomaly = recipientQueryResult.AnomalyEvaluation.IsAnomaly,
                Severity = recipientQueryResult.AnomalyEvaluation.Severity,
                Explanation = recipientQueryResult.AnomalyEvaluation.Explanation
            };
        }

        return JsonSerializer.Serialize(payload, JsonOptions);
    }
}

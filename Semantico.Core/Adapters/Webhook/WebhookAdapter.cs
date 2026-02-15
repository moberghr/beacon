using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models;

namespace Semantico.Core.Adapters.Webhook;

internal class WebhookAdapter(IHttpClientFactory httpClientFactory, SemanticoConfiguration configuration, ILogger<WebhookAdapter> logger) : IAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public NotificationType NotificationType => NotificationType.Webhook;

    public async Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int? lastNotificationResultCount)
    {
        var client = httpClientFactory.CreateClient();
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

        // Use body template if provided, otherwise use default JSON payload
        string bodyContent;
        if (!string.IsNullOrWhiteSpace(recipientQueryResult.BodyTemplate))
        {
            try
            {
                bodyContent = WebhookTemplateEngine.ProcessTemplate(recipientQueryResult.BodyTemplate, payload);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process webhook body template, falling back to default payload");
                bodyContent = JsonSerializer.Serialize(payload, JsonOptions);
            }
        }
        else
        {
            bodyContent = JsonSerializer.Serialize(payload, JsonOptions);
        }

        var content = new StringContent(bodyContent, Encoding.UTF8, System.Net.Mime.MediaTypeNames.Application.Json);

        // Parse and add custom headers
        if (!string.IsNullOrEmpty(recipientQueryResult.HeadersJson))
        {
            try
            {
                var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(recipientQueryResult.HeadersJson);
                if (headers != null)
                {
                    foreach (var (key, value) in headers)
                    {
                        client.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
                    }
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Failed to parse custom headers JSON for webhook recipient");
            }
        }

        var response = await client.PostAsync(recipientQueryResult.RecipientDestination, content);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            logger.LogError("Webhook returned error {StatusCode}: {ErrorBody}", response.StatusCode, errorBody);
            throw new SemanticoException(
                $"Failed to send Webhook notification: {response.StatusCode}. {errorBody}");
        }
    }
}

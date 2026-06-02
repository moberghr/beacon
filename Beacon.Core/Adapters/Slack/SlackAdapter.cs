using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Beacon.Core.Adapters.Slack;

/// <summary>
/// Adapter for sending query result notifications to Slack via incoming webhooks.
/// Uses ASCII-style tables in code blocks for data display.
/// </summary>
internal class SlackAdapter : IAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SlackMessageBuilder _messageBuilder;
    private readonly BeaconConfiguration _configuration;
    private readonly ILogger<SlackAdapter> _logger;

    public SlackAdapter(
        IHttpClientFactory httpClientFactory,
        BeaconConfiguration configuration,
        ILogger<SlackAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;

        // Initialize message builder with table formatter
        var tableFormatter = new SlackTableFormatter();
        _messageBuilder = new SlackMessageBuilder(tableFormatter, configuration);
    }

    public NotificationType NotificationType => NotificationType.Slack;

    public async Task SendNotificationAsync(
        RecipientQueryResult recipientQueryResult,
        int? lastNotificationResultCount,
        CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient();
        var queryResult = recipientQueryResult.QueryResult;

        string jsonPayload;

        if (!string.IsNullOrWhiteSpace(recipientQueryResult.BodyTemplate))
        {
            try
            {
                var templateData = Shared.TemplateDataBuilder.BuildTemplateData(recipientQueryResult, lastNotificationResultCount, _configuration.BaseUrl);
                jsonPayload = Shared.AdapterTemplateProcessor.ProcessTemplate(recipientQueryResult.BodyTemplate, templateData);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process custom Slack body template, falling back to default message");
                jsonPayload = BuildDefaultSlackMessage(recipientQueryResult, queryResult);
            }
        }
        else
        {
            jsonPayload = BuildDefaultSlackMessage(recipientQueryResult, queryResult);
        }

        var content = new StringContent(jsonPayload, Encoding.UTF8, System.Net.Mime.MediaTypeNames.Application.Json);
        var response = await client.PostAsync(recipientQueryResult.RecipientDestination, content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Slack webhook returned error {StatusCode}: {ErrorBody}", response.StatusCode, errorBody);
            throw new BeaconException(
                $"Failed to send Slack notification: {response.StatusCode}. {errorBody}");
        }
    }

    private string BuildDefaultSlackMessage(RecipientQueryResult recipientQueryResult, QueryResult queryResult)
    {
        // Build Slack message with blocks (including anomaly context if present)
        var message = _messageBuilder.BuildNotificationMessage(
            queryResult,
            recipientQueryResult.NotificationId,
            recipientQueryResult.AnomalyEvaluation);

        // Serialize to JSON with snake_case naming
        return JsonSerializer.Serialize(message, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
    }
}

/// <summary>
/// Represents a Slack message with text and blocks.
/// </summary>
internal class SlackMessage
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = null!;

    [JsonPropertyName("blocks")]
    public List<object> Blocks { get; set; } = [];
}

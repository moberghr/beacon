using Microsoft.Extensions.Logging;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Semantico.Core.Adapters.Slack;

/// <summary>
/// Adapter for sending query result notifications to Slack via incoming webhooks.
/// Uses ASCII-style tables in code blocks for data display.
/// </summary>
internal class SlackAdapter : IAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SlackMessageBuilder _messageBuilder;
    private readonly ILogger<SlackAdapter> _logger;

    public SlackAdapter(
        IHttpClientFactory httpClientFactory,
        SemanticoConfiguration configuration,
        ILogger<SlackAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        // Initialize message builder with table formatter
        var tableFormatter = new SlackTableFormatter();
        _messageBuilder = new SlackMessageBuilder(tableFormatter, configuration);
    }

    public NotificationType NotificationType => NotificationType.Slack;

    public async Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int? lastNotificationResultCount)
    {
        var client = _httpClientFactory.CreateClient();
        var queryResult = recipientQueryResult.QueryResult;

        // Build Slack message with blocks
        var message = _messageBuilder.BuildNotificationMessage(queryResult, recipientQueryResult.NotificationId);

        // Serialize to JSON with snake_case naming
        var jsonPayload = JsonSerializer.Serialize(message, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        // Send to Slack webhook
        var content = new StringContent(jsonPayload, Encoding.UTF8, System.Net.Mime.MediaTypeNames.Application.Json);
        var response = await client.PostAsync(recipientQueryResult.RecipientDestination, content);

        // Handle errors
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogError("Slack webhook returned error {StatusCode}: {ErrorBody}", response.StatusCode, errorBody);
            throw new SemanticoException(
                $"Failed to send Slack notification: {response.StatusCode}. {errorBody}");
        }
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

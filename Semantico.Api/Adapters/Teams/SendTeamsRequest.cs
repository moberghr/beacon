using System.Text.Json.Serialization;

namespace Semantico.Api.Adapters.Teams;

public class SendTeamsRequest
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "message";

    [JsonPropertyName("attachments")]
    public required Attachments[] Attachments { get; set; }
}

public class Attachments
{
    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } = "application/vnd.microsoft.card.adaptive";

    [JsonPropertyName("contentUrl")]
    public string? ContentUrl { get; set; }

    [JsonPropertyName("content")]
    public required Content Content { get; set; }
}
public class Content
{
    [JsonPropertyName("$schema")]
    public string Schema { get; set; } = "http://adaptivecards.io/schemas/adaptive-card.json";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "AdaptiveCard";

    [JsonPropertyName("version")]
    public string Version { get; init; } = "1.4";

    [JsonPropertyName("body")]
    public required AdaptiveCardElement[] Body { get; set; }
}

public class AdaptiveCardElement
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "TextBlock";

    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

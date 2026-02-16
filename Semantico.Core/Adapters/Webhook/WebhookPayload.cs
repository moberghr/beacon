namespace Semantico.Core.Adapters.Webhook;

public class WebhookPayload
{
    public DateTime Timestamp { get; set; }
    public string Source { get; set; } = "Semantico";
    public string SubscriptionName { get; set; } = null!;
    public int? SubscriptionId { get; set; }
    public string DataSourceName { get; set; } = null!;
    public string SqlQuery { get; set; } = null!;
    public int TotalRecords { get; set; }
    public double ExecutionTimeMs { get; set; }
    public int? PreviousResultCount { get; set; }
    public bool TimedOut { get; set; }
    public List<IDictionary<string, object?>> Records { get; set; } = [];
    public WebhookAnomalyData? Anomaly { get; set; }
    public string? NotificationUrl { get; set; }
}

public class WebhookAnomalyData
{
    public bool IsAnomaly { get; set; }
    public string Severity { get; set; } = null!;
    public string? Explanation { get; set; }
}

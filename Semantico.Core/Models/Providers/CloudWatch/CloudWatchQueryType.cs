namespace Semantico.Core.Models.Providers.CloudWatch;

public enum CloudWatchQueryType
{
    /// <summary>
    /// CloudWatch Logs Insights query
    /// Example: fields @timestamp, @message | filter @message like /ERROR/ | limit 100
    /// </summary>
    LogsInsights = 1,

    /// <summary>
    /// CloudWatch Metrics query (future support)
    /// </summary>
    Metrics = 2
}

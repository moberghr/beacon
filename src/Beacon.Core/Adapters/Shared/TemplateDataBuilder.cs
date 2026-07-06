namespace Beacon.Core.Adapters.Shared;

/// <summary>
/// Builds a dictionary of template variables that can be used by any adapter
/// </summary>
internal static class TemplateDataBuilder
{
    /// <summary>
    /// Creates a dictionary of all available template variables from the query result
    /// </summary>
    public static Dictionary<string, object?> BuildTemplateData(
        RecipientQueryResult recipientQueryResult,
        int? lastNotificationResultCount,
        string? baseUrl = null)
    {
        var queryResult = recipientQueryResult.QueryResult;

        var data = new Dictionary<string, object?>
        {
            ["Timestamp"] = DateTime.UtcNow,
            ["Source"] = "Beacon",
            ["SubscriptionName"] = queryResult.SubscriptionName,
            ["SubscriptionId"] = queryResult.SubscriptionId,
            ["DataSourceName"] = queryResult.DataSourceName,
            ["SqlQuery"] = queryResult.ShowQuery ? queryResult.SqlQuery : null,
            ["TotalRecords"] = queryResult.TotalRecords,
            ["ExecutionTimeMs"] = queryResult.ExecutionTimeMs,
            ["PreviousResultCount"] = lastNotificationResultCount,
            ["TimedOut"] = queryResult.TimedOut,
            ["Records"] = queryResult.TopRecords,
            ["NotificationUrl"] = !string.IsNullOrEmpty(baseUrl) && recipientQueryResult.NotificationId.HasValue
                ? $"{baseUrl.TrimEnd('/')}/notifications/{recipientQueryResult.NotificationId}"
                : null
        };

        // Add anomaly data if present
        if (recipientQueryResult.AnomalyEvaluation != null)
        {
            data["Anomaly"] = new Dictionary<string, object?>
            {
                ["IsAnomaly"] = recipientQueryResult.AnomalyEvaluation.IsAnomaly,
                ["Severity"] = recipientQueryResult.AnomalyEvaluation.Severity,
                ["Explanation"] = recipientQueryResult.AnomalyEvaluation.Explanation
            };
        }
        else
        {
            data["Anomaly"] = null;
        }

        return data;
    }
}

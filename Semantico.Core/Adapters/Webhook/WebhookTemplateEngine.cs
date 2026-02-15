using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Semantico.Core.Adapters.Webhook;

internal static partial class WebhookTemplateEngine
{
    [GeneratedRegex(@"\{\{([^}]+)\}\}", RegexOptions.Compiled)]
    private static partial Regex PlaceholderRegex();

    /// <summary>
    /// Processes the body template and replaces placeholders with actual values from the payload
    /// </summary>
    public static string ProcessTemplate(string template, WebhookPayload payload)
    {
        var result = template;
        var matches = PlaceholderRegex().Matches(template);

        foreach (Match match in matches)
        {
            var placeholder = match.Groups[1].Value.Trim();
            var value = GetPlaceholderValue(placeholder, payload);
            result = result.Replace(match.Value, value);
        }

        return result;
    }

    private static string GetPlaceholderValue(string placeholder, WebhookPayload payload)
    {
        try
        {
            // Handle nested properties (e.g., Anomaly.IsAnomaly)
            var parts = placeholder.Split('.');

            return parts[0].ToLowerInvariant() switch
            {
                "timestamp" => payload.Timestamp.ToString("o"),
                "source" => payload.Source,
                "subscriptionname" => payload.SubscriptionName,
                "subscriptionid" => payload.SubscriptionId?.ToString() ?? "",
                "datasourcename" => payload.DataSourceName,
                "sqlquery" => payload.SqlQuery ?? "",
                "totalrecords" => payload.TotalRecords.ToString(),
                "executiontimems" => payload.ExecutionTimeMs.ToString("F2"),
                "previousresultcount" => payload.PreviousResultCount?.ToString() ?? "",
                "timedout" => payload.TimedOut.ToString().ToLower(),
                "notificationurl" => payload.NotificationUrl ?? "",
                "records" => JsonSerializer.Serialize(payload.Records, new JsonSerializerOptions
                {
                    WriteIndented = false
                }),
                "records_pretty" => JsonSerializer.Serialize(payload.Records, new JsonSerializerOptions
                {
                    WriteIndented = true
                }),
                "anomaly" when parts.Length > 1 => GetAnomalyValue(parts[1], payload.Anomaly),
                "anomaly" => JsonSerializer.Serialize(payload.Anomaly),
                _ => $"{{{{UNKNOWN:{placeholder}}}}}"
            };
        }
        catch
        {
            return $"{{{{ERROR:{placeholder}}}}}";
        }
    }

    private static string GetAnomalyValue(string property, WebhookAnomalyData? anomaly)
    {
        if (anomaly == null)
            return "";

        return property.ToLowerInvariant() switch
        {
            "isanomaly" => anomaly.IsAnomaly.ToString().ToLower(),
            "severity" => anomaly.Severity,
            "explanation" => anomaly.Explanation ?? "",
            _ => $"{{{{UNKNOWN:Anomaly.{property}}}}}"
        };
    }

    /// <summary>
    /// Returns a default template example for documentation
    /// </summary>
    public static string GetDefaultTemplateExample()
    {
        return """
        {
          "timestamp": "{{Timestamp}}",
          "subscription": "{{SubscriptionName}}",
          "dataSource": "{{DataSourceName}}",
          "totalRecords": {{TotalRecords}},
          "executionTime": {{ExecutionTimeMs}},
          "timedOut": {{TimedOut}},
          "records": {{Records}},
          "notificationUrl": "{{NotificationUrl}}"
        }
        """;
    }
}

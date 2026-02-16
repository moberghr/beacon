using Semantico.Core.Data.Enums;

namespace Semantico.Core.Adapters.Shared;

/// <summary>
/// Provides example body templates for different adapter types
/// </summary>
public static class AdapterTemplateExamples
{
    /// <summary>
    /// Gets an example template for the specified notification type
    /// </summary>
    public static string GetExample(NotificationType notificationType)
    {
        return notificationType switch
        {
            NotificationType.Webhook => GetWebhookExample(),
            NotificationType.Teams => GetTeamsExample(),
            NotificationType.Slack => GetSlackExample(),
            _ => GetWebhookExample()
        };
    }

    private static string GetWebhookExample()
    {
        return """
        {
          "timestamp": "{{Timestamp}}",
          "subscription": "{{SubscriptionName}}",
          "dataSource": "{{DataSourceName}}",
          "alert": {
            "totalRecords": {{TotalRecords}},
            "executionTime": {{ExecutionTimeMs}},
            "timedOut": {{TimedOut}}
          },
          "data": {{Records}},
          "url": "{{NotificationUrl}}"
        }
        """;
    }

    private static string GetTeamsExample()
    {
        return """
        {
          "type": "message",
          "attachments": [
            {
              "contentType": "application/vnd.microsoft.card.adaptive",
              "content": {
                "type": "AdaptiveCard",
                "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                "version": "1.5",
                "body": [
                  {
                    "type": "TextBlock",
                    "text": "Semantico Alert",
                    "weight": "Bolder",
                    "size": "Large"
                  },
                  {
                    "type": "TextBlock",
                    "text": "{{DataSourceName}}: {{SubscriptionName}}",
                    "weight": "Bolder",
                    "size": "Medium",
                    "wrap": true
                  },
                  {
                    "type": "FactSet",
                    "facts": [
                      {
                        "title": "Total Records:",
                        "value": "{{TotalRecords}}"
                      },
                      {
                        "title": "Execution Time:",
                        "value": "{{ExecutionTimeMs}}ms"
                      }
                    ]
                  }
                ],
                "actions": [
                  {
                    "type": "Action.OpenUrl",
                    "title": "View Details",
                    "url": "{{NotificationUrl}}"
                  }
                ]
              }
            }
          ]
        }
        """;
    }

    private static string GetSlackExample()
    {
        return """
        {
          "text": "Semantico Alert: {{SubscriptionName}}",
          "blocks": [
            {
              "type": "header",
              "text": {
                "type": "plain_text",
                "text": "Semantico Alert"
              }
            },
            {
              "type": "section",
              "text": {
                "type": "mrkdwn",
                "text": "*{{DataSourceName}}*: {{SubscriptionName}}"
              }
            },
            {
              "type": "section",
              "fields": [
                {
                  "type": "mrkdwn",
                  "text": "*Total Records:*\n{{TotalRecords}}"
                },
                {
                  "type": "mrkdwn",
                  "text": "*Execution Time:*\n{{ExecutionTimeMs}}ms"
                }
              ]
            },
            {
              "type": "actions",
              "elements": [
                {
                  "type": "button",
                  "text": {
                    "type": "plain_text",
                    "text": "View Details"
                  },
                  "url": "{{NotificationUrl}}"
                }
              ]
            }
          ]
        }
        """;
    }

    /// <summary>
    /// Gets a list of all available placeholder variables
    /// </summary>
    public static string GetAvailablePlaceholders()
    {
        return """
        Available placeholders:

        Basic Info:
        - {{Timestamp}} - Current UTC timestamp
        - {{Source}} - Always "Semantico"
        - {{SubscriptionName}} - Name of the subscription
        - {{SubscriptionId}} - ID of the subscription
        - {{DataSourceName}} - Name of the data source

        Query Results:
        - {{SqlQuery}} - The SQL query (if enabled)
        - {{TotalRecords}} - Total number of records
        - {{ExecutionTimeMs}} - Query execution time in milliseconds
        - {{PreviousResultCount}} - Result count from previous execution
        - {{TimedOut}} - Whether the query timed out (true/false)
        - {{Records}} - JSON array of records
        - {{Records_Pretty}} - Formatted JSON array of records

        Anomaly Detection (if enabled):
        - {{Anomaly.IsAnomaly}} - Whether an anomaly was detected (true/false)
        - {{Anomaly.Severity}} - Severity level (Low/Medium/High/Critical)
        - {{Anomaly.Explanation}} - AI explanation of the anomaly

        Links:
        - {{NotificationUrl}} - URL to view notification details
        """;
    }
}

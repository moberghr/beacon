using System.Text;
using Beacon.Core.Adapters.Shared;
using Beacon.Core.Models.Anomaly;

namespace Beacon.Core.Adapters.Slack;

/// <summary>
/// Builds Slack block messages for query result notifications.
/// </summary>
internal class SlackMessageBuilder
{
    private readonly SlackTableFormatter _tableFormatter;
    private readonly BeaconConfiguration _configuration;

    public SlackMessageBuilder(SlackTableFormatter tableFormatter, BeaconConfiguration configuration)
    {
        _tableFormatter = tableFormatter;
        _configuration = configuration;
    }

    /// <summary>
    /// Builds a complete Slack message with blocks for a query result.
    /// </summary>
    public SlackMessage BuildNotificationMessage(
        QueryResult queryResult,
        int? notificationId,
        AnomalyEvaluationResult? anomalyEvaluation = null)
    {
        var blocks = new List<object>();

        // Header block (with anomaly indicator if present)
        blocks.Add(BuildHeaderBlock(queryResult, anomalyEvaluation));

        // Anomaly alert block (if anomaly detected)
        if (anomalyEvaluation?.IsAnomaly == true)
        {
            blocks.Add(BuildAnomalyAlertBlock(anomalyEvaluation));
        }

        // Query block (if ShowQuery is enabled)
        if (queryResult.ShowQuery)
        {
            blocks.Add(BuildQueryBlock(queryResult.SqlQuery));
        }

        // Divider
        blocks.Add(new { type = "divider" });

        // Summary section
        blocks.Add(BuildSummaryBlock(queryResult));

        // View full results button (if BaseUrl configured)
        if (!string.IsNullOrEmpty(_configuration.BaseUrl) && notificationId.HasValue)
        {
            blocks.Add(BuildActionsBlock(_configuration.BaseUrl, notificationId.Value));
        }

        // Data table (if records exist)
        if (queryResult.TopRecords.HasRecords())
        {
            var tableBlock = _tableFormatter.GenerateTableBlock(queryResult.TopRecords);
            blocks.Add(tableBlock);
        }

        return new SlackMessage
        {
            Text = $"{AdapterConstants.NotificationPrefix} {queryResult.DataSourceName} - {queryResult.SubscriptionName}",
            Blocks = blocks
        };
    }

    private object BuildHeaderBlock(QueryResult queryResult, AnomalyEvaluationResult? anomalyEvaluation)
    {
        var prefix = anomalyEvaluation?.IsAnomaly == true
            ? "🔴 ANOMALY DETECTED"
            : AdapterConstants.NotificationPrefix;

        return new
        {
            type = "header",
            text = new
            {
                type = "plain_text",
                text = $"{prefix} {queryResult.DataSourceName} - {queryResult.SubscriptionName}"
            }
        };
    }

    private object BuildAnomalyAlertBlock(AnomalyEvaluationResult anomalyEvaluation)
    {
        var severityEmoji = anomalyEvaluation.Severity switch
        {
            "Critical" => "🚨",
            "High" => "🔴",
            "Medium" => "🟡",
            _ => "🔵"
        };

        var details = new StringBuilder();
        details.AppendLine($"*{severityEmoji} Anomaly Severity:* {anomalyEvaluation.Severity}");
        details.AppendLine($"*Explanation:* {anomalyEvaluation.Explanation}");

        if (anomalyEvaluation.BaselineMean.HasValue)
        {
            details.AppendLine($"*Baseline:* {anomalyEvaluation.BaselineMean:N0} (from {anomalyEvaluation.HistoricalDataPoints} data points)");
        }

        if (anomalyEvaluation.ZScore.HasValue)
        {
            details.AppendLine($"*Deviation:* {Math.Abs(anomalyEvaluation.ZScore.Value):N2}σ from baseline");
        }

        return new
        {
            type = "section",
            text = new
            {
                type = "mrkdwn",
                text = details.ToString()
            }
        };
    }

    private object BuildQueryBlock(string sqlQuery)
    {
        return new
        {
            type = "section",
            text = new
            {
                type = "mrkdwn",
                text = $"*Query:*\n```sql\n{sqlQuery}\n```"
            }
        };
    }

    private object BuildSummaryBlock(QueryResult queryResult)
    {
        var summaryText = queryResult.TopRecords.HasRecords()
            ? $"*Results:* Showing {queryResult.TopRecords.Count} of {queryResult.TotalRecords} total records"
            : $"*Results:* Query executed successfully. Total records: {queryResult.TotalRecords}";

        return new
        {
            type = "section",
            text = new
            {
                type = "mrkdwn",
                text = summaryText
            }
        };
    }

    private object BuildActionsBlock(string baseUrl, int notificationId)
    {
        return new
        {
            type = "actions",
            elements = new[]
            {
                new
                {
                    type = "button",
                    text = new
                    {
                        type = "plain_text",
                        text = "View Full Results"
                    },
                    url = $"{baseUrl.TrimEnd('/')}/notifications/{notificationId}"
                }
            }
        };
    }
}

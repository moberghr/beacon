using System.Text;
using Beacon.Core.Adapters.Shared;
using Beacon.Core.Helpers;
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

        // The table is built first: the summary states how many of its rows it really shows
        object? tableBlock = null;
        var shownRows = 0;
        if (queryResult.TopRecords.HasRecords())
        {
            tableBlock = _tableFormatter.GenerateTableBlock(queryResult.TopRecords, out shownRows);
        }

        // Summary section
        blocks.Add(BuildSummaryBlock(queryResult, shownRows));

        // View full results button (if BaseUrl configured)
        var resultsLink = notificationId.HasValue
            ? BaseUrlPolicy.Link(_configuration.BaseUrl, $"notifications/{notificationId}")
            : null;
        if (resultsLink != null)
        {
            blocks.Add(BuildActionsBlock(resultsLink));
        }

        // Data table (if records exist)
        if (tableBlock != null)
        {
            blocks.Add(tableBlock);
        }

        return new SlackMessage
        {
            Text = ChannelText.TruncateEscaped(ChannelText.Slack($"{AdapterConstants.NotificationPrefix} {queryResult.DataSourceName} - {queryResult.SubscriptionName}"), AdapterConstants.Slack.MaxSectionTextLength),
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
                text = ChannelText.Truncate(ChannelText.StripUnsafe($"{prefix} {queryResult.DataSourceName} - {queryResult.SubscriptionName}").Replace('\n', ' ').Replace('\r', ' '), AdapterConstants.Slack.MaxHeaderLength)
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
        details.AppendLine($"*{severityEmoji} Anomaly Severity:* {ChannelText.Slack(anomalyEvaluation.Severity)}");
        details.AppendLine($"*Explanation:* {ChannelText.TruncateEscaped(ChannelText.Slack(anomalyEvaluation.Explanation), AdapterConstants.Slack.MaxAnomalyExplanationLength)}");

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
                text = ChannelText.TruncateEscaped(details.ToString(), AdapterConstants.Slack.MaxSectionTextLength)
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
                text = $"*Query:*\n```sql\n{ChannelText.TruncateEscaped(ChannelText.SlackFenced(sqlQuery), AdapterConstants.Slack.MaxSectionTextLength - 40)}\n```"
            }
        };
    }

    private object BuildSummaryBlock(QueryResult queryResult, int shownRows)
    {
        var summaryText = queryResult.TopRecords.HasRecords()
            ? $"*Results:* Showing {shownRows} of {queryResult.TotalRecords} total records"
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

    private object BuildActionsBlock(string resultsLink)
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
                    url = resultsLink
                }
            }
        };
    }
}

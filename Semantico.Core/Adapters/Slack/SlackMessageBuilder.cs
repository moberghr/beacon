using Semantico.Core.Adapters.Shared;

namespace Semantico.Core.Adapters.Slack;

/// <summary>
/// Builds Slack block messages for query result notifications.
/// </summary>
internal class SlackMessageBuilder
{
    private readonly SlackTableFormatter _tableFormatter;
    private readonly SemanticoConfiguration _configuration;

    public SlackMessageBuilder(SlackTableFormatter tableFormatter, SemanticoConfiguration configuration)
    {
        _tableFormatter = tableFormatter;
        _configuration = configuration;
    }

    /// <summary>
    /// Builds a complete Slack message with blocks for a query result.
    /// </summary>
    public SlackMessage BuildNotificationMessage(QueryResult queryResult, int? notificationId)
    {
        var blocks = new List<object>();

        // Header block
        blocks.Add(BuildHeaderBlock(queryResult));

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

    private object BuildHeaderBlock(QueryResult queryResult)
    {
        return new
        {
            type = "header",
            text = new
            {
                type = "plain_text",
                text = $"{AdapterConstants.NotificationPrefix} {queryResult.DataSourceName} - {queryResult.SubscriptionName}"
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

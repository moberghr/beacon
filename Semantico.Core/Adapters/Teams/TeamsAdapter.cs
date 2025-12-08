using MessageCardModel;
using Semantico.Core.Data.Enums;
using System.Text;

namespace Semantico.Core.Adapters.Teams;

internal class TeamsAdapter(IHttpClientFactory httpClientFactory, SemanticoConfiguration configuration) : IAdapter
{
    public NotificationType NotificationType => NotificationType.Teams;

    public async Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int? lastNotificationResultCount)
    {
        var client = httpClientFactory.CreateClient();
        var queryResult = recipientQueryResult.QueryResult;

        var bodyElements = new List<AdaptiveCards.AdaptiveElement>
        {
            new AdaptiveCards.AdaptiveTextBlock()
            {
                Text = $"[Semantico] {queryResult.DataSourceName} - {queryResult.SubscriptionName}",
                Size = AdaptiveCards.AdaptiveTextSize.Large,
                Weight = AdaptiveCards.AdaptiveTextWeight.Bolder,
                Id = "title",
            }
        };

        if (queryResult.ShowQuery)
        {
            bodyElements.Add(new AdaptiveCards.AdaptiveTextBlock
            {
                Text = $"Query = {queryResult.SqlQuery}",
                Wrap = true,
                Id = "queryText"
            });
        }

        if (queryResult.TopRecords.Count > 0)
        {
            bodyElements.Add(new AdaptiveCards.AdaptiveTextBlock()
            {
                Text = queryResult.TotalRecords > 10 ? "First 10 records" : "Query Results",
                Weight = AdaptiveCards.AdaptiveTextWeight.Bolder,
                Spacing = AdaptiveCards.AdaptiveSpacing.Medium,
                Id = "first10RecordsTitle"
            });
            bodyElements.Add(GenerateAdaptiveTableFromQueryResults(queryResult.TopRecords));
        }
        else
        {
            bodyElements.Add(new AdaptiveCards.AdaptiveTextBlock()
            {
                Text = $"Query executed successfully. Total records: {queryResult.TotalRecords}",
                Spacing = AdaptiveCards.AdaptiveSpacing.Medium,
                Id = "noResultsText"
            });
        }

        var card = new AdaptiveCards.AdaptiveCard("1.5")
        {
            Type = "AdaptiveCard",
            Speak = $"{queryResult.DataSourceName} - {queryResult.SubscriptionName}",
            Body = bodyElements,
            Actions = string.IsNullOrEmpty(configuration.BaseUrl) || !recipientQueryResult.NotificationId.HasValue
                ? new List<AdaptiveCards.AdaptiveAction>()
                : new List<AdaptiveCards.AdaptiveAction>
                {
                    new AdaptiveCards.AdaptiveOpenUrlAction()
                    {
                        Title = "View Query Results",
                        Url = new Uri($"{configuration.BaseUrl.TrimEnd('/')}/notifications/{recipientQueryResult.NotificationId}"),
                    }
                }
        };

        var jsonPayload = card.ToJson();
        var content = new StringContent(jsonPayload, Encoding.UTF8, System.Net.Mime.MediaTypeNames.Application.Json);

        await client.PostAsync(recipientQueryResult.RecipientDestination, content);
    }

    private AdaptiveCards.AdaptiveTable GenerateAdaptiveTableFromQueryResults(List<IDictionary<string, object?>> queryResults)
    {
        if (!queryResults.Any())
        {
            return new AdaptiveCards.AdaptiveTable();
        }

        var columnNames = queryResults.First().Keys.Take(3).ToList();
        
        // Create columns
        var columns = columnNames.Select(_ => new AdaptiveCards.AdaptiveTableColumnDefinition
        {
            Width = 1
        }).ToList();

        // Create header row
        var headerRow = new AdaptiveCards.AdaptiveTableRow
        {
            Cells = columnNames.Select(columnName => new AdaptiveCards.AdaptiveTableCell
            {
                Type = "TableCell",
                Items =
                [
                    new AdaptiveCards.AdaptiveTextBlock
                    {
                        Text = columnName,
                        Weight = AdaptiveCards.AdaptiveTextWeight.Bolder
                    }
                ]
            }).ToList()
        };

        // Create data rows
        var dataRows = queryResults.Select(result => new AdaptiveCards.AdaptiveTableRow
        {
            Cells = columnNames.Select(columnName => new AdaptiveCards.AdaptiveTableCell
            {
                Type = "TableCell",
                Items =
                [
                    new AdaptiveCards.AdaptiveTextBlock
                    {
                        Text = result.TryGetValue(columnName, out var value) ? value?.ToString() ?? string.Empty : string.Empty,
                        Wrap = true
                    }
                ]
            }).ToList()
        }).ToList();

        // Combine header and data rows
        var allRows = new List<AdaptiveCards.AdaptiveTableRow> { headerRow };
        allRows.AddRange(dataRows);

        return new AdaptiveCards.AdaptiveTable
        {
            Columns = columns,
            Rows = allRows,
            FirstRowAsHeaders = true,
            ShowGridLines = true,
            GridStyle = AdaptiveCards.AdaptiveContainerStyle.Emphasis
        };
    }
}
using MessageCardModel;
using Semantico.Core.Data.Enums;
using System.Text;

namespace Semantico.Core.Adapters.Teams;

internal class TeamsAdapter(IHttpClientFactory httpClientFactory) : IAdapter
{
    public NotificationType NotificationType => NotificationType.Teams;

    public async Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int? lastNotificationResultCount)
    {
        var client = httpClientFactory.CreateClient();

        var card = new AdaptiveCards.AdaptiveCard("1.5")
        {
            Type = "AdaptiveCard",
            Speak = $"{recipientQueryResult.QueryResult.ProjectName} - {recipientQueryResult.QueryResult.SubscriptionName}",
            Body = new List<AdaptiveCards.AdaptiveElement>
            {
                new AdaptiveCards.AdaptiveTextBlock()
                {
                    Text = $"[Semantico] {recipientQueryResult.QueryResult.ProjectName} - {recipientQueryResult.QueryResult.SubscriptionName}",
                    Size = AdaptiveCards.AdaptiveTextSize.Large,
                    Weight = AdaptiveCards.AdaptiveTextWeight.Bolder,
                    Id = "title",
                },
                new AdaptiveCards.AdaptiveTextBlock
                {
                    Text = $"Query = {recipientQueryResult.QueryResult.SqlQuery}",
                    Wrap = true,
                    Id = "queryText"
                },
                new AdaptiveCards.AdaptiveTextBlock()
                {
                    Text = recipientQueryResult.QueryResult.TotalRecords > 10 ? "First 10 records" : "Query Results",
                    Weight = AdaptiveCards.AdaptiveTextWeight.Bolder,
                    Spacing = AdaptiveCards.AdaptiveSpacing.Medium,
                    Id = "first10RecordsTitle"
                },
                GenerateAdaptiveTableFromQueryResults(recipientQueryResult.QueryResult.TopRecords)
            },
            Actions = new List<AdaptiveCards.AdaptiveAction>
            {
                new AdaptiveCards.AdaptiveOpenUrlAction()
                {
                    Title = "View Query Results",
                    Url = new Uri($"https://api.servicedesk.aur.is/semantico/subscriptions/details/{recipientQueryResult.QueryResult.SubscriptionId}"),
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

        var columnNames = queryResults.First().Keys.ToList();
        
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
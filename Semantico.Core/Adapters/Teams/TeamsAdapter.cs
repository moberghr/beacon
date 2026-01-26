using MessageCardModel;
using Microsoft.Extensions.Logging;
using Semantico.Core.Adapters.Shared;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models;
using System.Text;

namespace Semantico.Core.Adapters.Teams;

internal class TeamsAdapter(IHttpClientFactory httpClientFactory, SemanticoConfiguration configuration, ILogger<TeamsAdapter> logger) : IAdapter
{
    private const int MaxColumns = AdapterConstants.Teams.MaxColumns;
    private const int MaxRows = AdapterConstants.Teams.MaxRows;

    public NotificationType NotificationType => NotificationType.Teams;

    public async Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int? lastNotificationResultCount)
    {
        var client = httpClientFactory.CreateClient();
        var queryResult = recipientQueryResult.QueryResult;

        var bodyElements = new List<AdaptiveCards.AdaptiveElement>
        {
            new AdaptiveCards.AdaptiveTextBlock()
            {
                Text = "Semantico",
                Size = AdaptiveCards.AdaptiveTextSize.Large,
                Weight = AdaptiveCards.AdaptiveTextWeight.Bolder,
                Id = "title",
            },
            new AdaptiveCards.AdaptiveTextBlock()
            {
                Text = $"{queryResult.DataSourceName}: {queryResult.SubscriptionName}",
                Size = AdaptiveCards.AdaptiveTextSize.Medium,
                Weight = AdaptiveCards.AdaptiveTextWeight.Bolder,
                Wrap = true,
                Id = "subtitle",
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

        if (queryResult.TopRecords.HasRecords())
        {
            bodyElements.Add(new AdaptiveCards.AdaptiveTextBlock()
            {
                Text = queryResult.TotalRecords > MaxRows ? $"First {MaxRows} records" : "Query Results",
                Weight = AdaptiveCards.AdaptiveTextWeight.Bolder,
                Spacing = AdaptiveCards.AdaptiveSpacing.Medium,
                Id = "firstRecordsTitle"
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

        var response = await client.PostAsync(recipientQueryResult.RecipientDestination, content);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            logger.LogError("Microsoft Teams webhook returned error {StatusCode}: {ErrorBody}", response.StatusCode, errorBody);
            throw new SemanticoException(
                $"Failed to send Teams notification: {response.StatusCode}. {errorBody}");
        }
    }

    private AdaptiveCards.AdaptiveTable GenerateAdaptiveTableFromQueryResults(List<IDictionary<string, object?>> queryResults)
    {
        if (!queryResults.HasRecords())
        {
            return new AdaptiveCards.AdaptiveTable();
        }

        var columnNames = queryResults.GetColumnNamesSafe(MaxColumns);
        
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
                        Text = result.TryGetValue(columnName, out var value) ? CellValueFormatter.Format(value) : string.Empty,
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
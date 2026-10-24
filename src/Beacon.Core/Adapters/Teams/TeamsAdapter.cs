using Microsoft.Extensions.Logging;
using Beacon.Core.Adapters.Shared;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Notifications;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Beacon.Core.Adapters.Teams;

internal class TeamsAdapter(NotificationHttpSender sender, BeaconConfiguration configuration, ILogger<TeamsAdapter> logger) : IAdapter
{
    private const int MaxColumns = AdapterConstants.Teams.MaxColumns;
    private const int MaxRows = AdapterConstants.Teams.MaxRows;

    public NotificationType NotificationType => NotificationType.Teams;

    public async Task SendNotificationAsync(
        RecipientQueryResult recipientQueryResult,
        int? lastNotificationResultCount,
        CancellationToken cancellationToken = default)
    {
        var queryResult = recipientQueryResult.QueryResult;

        string jsonPayload;
        if (!string.IsNullOrWhiteSpace(recipientQueryResult.BodyTemplate))
        {
            try
            {
                var templateData = Shared.TemplateDataBuilder.BuildTemplateData(recipientQueryResult, lastNotificationResultCount, configuration.BaseUrl);
                jsonPayload = Shared.AdapterTemplateProcessor.ProcessTemplate(recipientQueryResult.BodyTemplate, templateData, escapeJsonStrings: true, escapeText: Shared.ChannelText.Teams);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to process custom Teams body template, falling back to default Adaptive Card");
                jsonPayload = BuildDefaultAdaptiveCard(recipientQueryResult, queryResult);
            }
        }
        else
        {
            jsonPayload = BuildDefaultAdaptiveCard(recipientQueryResult, queryResult);
        }

        var content = new StringContent(jsonPayload, Encoding.UTF8, System.Net.Mime.MediaTypeNames.Application.Json);

        await sender.PostAsync(NotificationType.Teams, recipientQueryResult.RecipientDestination, content, headers: null, cancellationToken);
    }

    // Teams refuses a card over 28 KB: rows are dropped (halving) until the serialized card fits, and the card says so.
    private string BuildDefaultAdaptiveCard(RecipientQueryResult recipientQueryResult, QueryResult queryResult)
    {
        var rows = queryResult.TopRecords.Count;
        var card = BuildDefaultAdaptiveCard(recipientQueryResult, queryResult, rows);
        while (rows > 0 && Encoding.UTF8.GetByteCount(card) > AdapterConstants.Teams.MaxCardBytes)
        {
            rows /= 2;
            card = BuildDefaultAdaptiveCard(recipientQueryResult, queryResult, rows);
        }

        return card;
    }

    private string BuildDefaultAdaptiveCard(RecipientQueryResult recipientQueryResult, QueryResult queryResult, int shownRows)
    {
        var bodyElements = new JsonArray
        {
            TextBlock("Beacon", id: "title", size: "large", weight: "bolder"),
            TextBlock(ChannelText.Teams(ChannelText.Truncate($"{queryResult.DataSourceName}: {queryResult.SubscriptionName}", AdapterConstants.Teams.MaxCellLength)), id: "subtitle", size: "medium", weight: "bolder", wrap: true),
        };

        if (queryResult.ShowQuery)
        {
            bodyElements.Add(TextBlock($"Query = {ChannelText.Teams(ChannelText.Truncate(queryResult.SqlQuery, AdapterConstants.Teams.MaxQueryLength))}", id: "queryText", wrap: true));
        }

        if (queryResult.TopRecords.HasRecords())
        {
            bodyElements.Add(TextBlock(
                queryResult.TotalRecords > MaxRows ? $"First {MaxRows} records" : "Query Results",
                id: "firstRecordsTitle",
                weight: "bolder",
                spacing: "medium"));
            if (shownRows > 0)
            {
                bodyElements.Add(GenerateAdaptiveTableFromQueryResults(queryResult.TopRecords.Take(shownRows).ToList()));
            }

            if (shownRows < queryResult.TopRecords.Count)
            {
                bodyElements.Add(TextBlock($"{ChannelText.TruncationMarker}: {shownRows} of {queryResult.TopRecords.Count} records shown", id: "truncatedText", spacing: "small"));
            }
        }
        else
        {
            bodyElements.Add(TextBlock($"Query executed successfully. Total records: {queryResult.TotalRecords}", id: "noResultsText", spacing: "medium"));
        }

        var card = new JsonObject
        {
            ["type"] = "AdaptiveCard",
            ["version"] = "1.5",
            ["speak"] = ChannelText.Truncate(ChannelText.StripUnsafe($"{queryResult.DataSourceName} - {queryResult.SubscriptionName}"), AdapterConstants.Teams.MaxCellLength),
            ["body"] = bodyElements,
        };

        var resultsLink = recipientQueryResult.NotificationId.HasValue
            ? BaseUrlPolicy.Link(configuration.BaseUrl, $"notifications/{recipientQueryResult.NotificationId}")
            : null;
        if (resultsLink != null)
        {
            card["actions"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "Action.OpenUrl",
                    ["url"] = resultsLink,
                    ["title"] = "View Query Results",
                },
            };
        }

        return Serialize(card);
    }

    private JsonObject GenerateAdaptiveTableFromQueryResults(List<IDictionary<string, object?>> queryResults)
    {
        var columnNames = queryResults.GetColumnNamesSafe(MaxColumns);

        var headerRow = TableRow(columnNames.Select(x => TableCell(TextBlock(Cell(x), weight: "bolder"))));

        var rows = new JsonArray { headerRow };
        foreach (var result in queryResults)
        {
            rows.Add(TableRow(columnNames.Select(x =>
                TableCell(TextBlock(result.TryGetValue(x, out var value) ? Cell(CellValueFormatter.Format(value)) : string.Empty, wrap: true)))));
        }

        var columns = new JsonArray();
        foreach (var _ in columnNames)
        {
            columns.Add(new JsonObject { ["width"] = 1 });
        }

        return new JsonObject
        {
            ["type"] = "Table",
            ["rows"] = rows,
            ["columns"] = columns,
            ["gridStyle"] = "emphasis",
            ["firstRowAsHeaders"] = true,
        };
    }

    private static string Cell(string text)
    {
        return ChannelText.Teams(ChannelText.Truncate(text, AdapterConstants.Teams.MaxCellLength));
    }

    private static JsonObject TableRow(IEnumerable<JsonObject> cells)
    {
        return new JsonObject
        {
            ["type"] = "TableRow",
            ["cells"] = new JsonArray(cells.Cast<JsonNode?>().ToArray()),
        };
    }

    private static JsonObject TableCell(JsonObject item)
    {
        return new JsonObject
        {
            ["type"] = "TableCell",
            ["items"] = new JsonArray { item },
        };
    }

    private static JsonObject TextBlock(string text, string? id = null, string? size = null, string? weight = null, bool wrap = false, string? spacing = null)
    {
        var block = new JsonObject { ["type"] = "TextBlock" };

        if (id != null)
        {
            block["id"] = id;
        }

        if (size != null)
        {
            block["size"] = size;
        }

        if (weight != null)
        {
            block["weight"] = weight;
        }

        block["text"] = text;

        if (wrap)
        {
            block["wrap"] = true;
        }

        if (spacing != null)
        {
            block["spacing"] = spacing;
        }

        return block;
    }

    // Strings are escaped by hand so the card keeps its established bytes: only quotes, backslashes, control
    // characters and the line separators are escaped; everything else (accents, emoji) is written as is.
    private static string Serialize(JsonNode card)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            WriteNode(writer, card);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteNode(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                writer.WriteStartObject();
                foreach (var property in jsonObject)
                {
                    writer.WritePropertyName(property.Key);
                    WriteNode(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonArray jsonArray:
                writer.WriteStartArray();
                foreach (var item in jsonArray)
                {
                    WriteNode(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValue jsonValue when jsonValue.TryGetValue<string>(out var text):
                writer.WriteRawValue(Escape(text));
                break;
            case null:
                writer.WriteNullValue();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }

    private static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length + 2);
        builder.Append('"');

        for (var i = 0; i < text.Length; i++)
        {
            var x = text[i];

            // A lone surrogate is written as U+FFFD, as the UTF-8 encoding of the old serializer's output turned it.
            if (char.IsSurrogate(x))
            {
                var pair = char.IsHighSurrogate(x) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
                if (pair)
                {
                    builder.Append(x).Append(text[++i]);
                }
                else
                {
                    builder.Append('\uFFFD');
                }

                continue;
            }

            switch (x)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case < ' ' or '\u0085' or '\u2028' or '\u2029':
                    builder.Append("\\u").Append(((int)x).ToString("x4"));
                    break;
                default:
                    builder.Append(x);
                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}

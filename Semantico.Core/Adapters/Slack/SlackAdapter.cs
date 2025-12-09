using Microsoft.Extensions.Logging;
using Semantico.Core.Data.Enums;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Semantico.Core.Adapters.Slack;

internal class SlackAdapter(IHttpClientFactory httpClientFactory, SemanticoConfiguration configuration, ILogger<SlackAdapter> logger) : IAdapter
{
    private const int MaxColumns = 8; // Balanced for table readability in code blocks
    private const int MaxRows = 50; // Code block can display many rows efficiently

    public NotificationType NotificationType => NotificationType.Slack;

    public async Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int? lastNotificationResultCount)
    {
        var client = httpClientFactory.CreateClient();
        var queryResult = recipientQueryResult.QueryResult;

        var message = BuildSlackMessage(queryResult, recipientQueryResult.NotificationId);
        var jsonPayload = JsonSerializer.Serialize(message, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        var content = new StringContent(jsonPayload, Encoding.UTF8, System.Net.Mime.MediaTypeNames.Application.Json);
        var response = await client.PostAsync(recipientQueryResult.RecipientDestination, content);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            logger.LogError("Slack API returned error {StatusCode}: {ErrorBody}", response.StatusCode, errorBody);
            throw new HttpRequestException($"Slack API returned {response.StatusCode}: {errorBody}");
        }
    }

    private SlackMessage BuildSlackMessage(QueryResult queryResult, int? notificationId)
    {
        var blocks = new List<object>();

        // Header block
        blocks.Add(new
        {
            type = "header",
            text = new
            {
                type = "plain_text",
                text = $"[Semantico] {queryResult.DataSourceName} - {queryResult.SubscriptionName}"
            }
        });

        // Query block (if ShowQuery is enabled)
        if (queryResult.ShowQuery)
        {
            blocks.Add(new
            {
                type = "section",
                text = new
                {
                    type = "mrkdwn",
                    text = $"*Query:*\n```sql\n{queryResult.SqlQuery}\n```"
                }
            });
        }

        // Divider
        blocks.Add(new { type = "divider" });

        // Summary section for results
        var summaryText = queryResult.TopRecords.Count > 0
            ? $"*Results:* Showing {queryResult.TopRecords.Count} of {queryResult.TotalRecords} total records"
            : $"*Results:* Query executed successfully. Total records: {queryResult.TotalRecords}";

        blocks.Add(new
        {
            type = "section",
            text = new
            {
                type = "mrkdwn",
                text = summaryText
            }
        });

        // Add action button if BaseUrl is configured
        if (!string.IsNullOrEmpty(configuration.BaseUrl) && notificationId.HasValue)
        {
            blocks.Add(new
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
                        url = $"{configuration.BaseUrl.TrimEnd('/')}/notifications/{notificationId}"
                    }
                }
            });
        }

        // Add formatted results as table (using code block for table-like layout)
        if (queryResult.TopRecords.Count > 0)
        {
            var tableBlock = GenerateTableCodeBlock(queryResult.TopRecords);
            blocks.Add(tableBlock);
        }

        var message = new SlackMessage
        {
            Text = $"[Semantico] {queryResult.DataSourceName} - {queryResult.SubscriptionName}",
            Blocks = blocks
        };

        return message;
    }

    private object GenerateTableCodeBlock(List<IDictionary<string, object?>> records)
    {
        if (!records.Any())
        {
            return new { type = "section", text = new { type = "plain_text", text = "No records to display" } };
        }

        // Get all columns (up to MaxColumns)
        var columnNames = records.First().Keys.Take(MaxColumns).ToList();

        // Calculate column widths based on content
        var columnWidths = new Dictionary<string, int>();
        foreach (var colName in columnNames)
        {
            // Start with column name length
            var maxWidth = colName.Length;

            // Check all values in this column
            foreach (var record in records.Take(MaxRows))
            {
                if (record.TryGetValue(colName, out var val))
                {
                    var formattedValue = FormatCellValue(val);
                    maxWidth = Math.Max(maxWidth, formattedValue.Length);
                }
            }

            // Cap width at reasonable maximum for readability
            columnWidths[colName] = Math.Min(maxWidth, 30);
        }

        // Build table as text
        var tableBuilder = new StringBuilder();

        // Header row
        var headerParts = columnNames.Select(col => PadOrTruncate(col, columnWidths[col]));
        tableBuilder.AppendLine(string.Join(" | ", headerParts));

        // Separator row
        var separatorParts = columnNames.Select(col => new string('-', columnWidths[col]));
        tableBuilder.AppendLine(string.Join("-+-", separatorParts));

        // Data rows
        foreach (var record in records.Take(MaxRows))
        {
            var rowParts = columnNames.Select(col =>
            {
                var value = record.TryGetValue(col, out var val) ? FormatCellValue(val) : string.Empty;
                return PadOrTruncate(value, columnWidths[col]);
            });
            tableBuilder.AppendLine(string.Join(" | ", rowParts));
        }

        // Wrap in code block for fixed-width display
        return new
        {
            type = "section",
            text = new
            {
                type = "mrkdwn",
                text = $"```\n{tableBuilder}```"
            }
        };
    }

    private string PadOrTruncate(string text, int width)
    {
        if (text.Length > width)
        {
            return text.Substring(0, width - 1) + "…";
        }
        return text.PadRight(width);
    }

    private List<object> GenerateFieldsBlocks_Alternative(List<IDictionary<string, object?>> records)
    {
        var blocks = new List<object>();

        if (!records.Any())
        {
            return blocks;
        }

        // Get all columns (up to MaxColumns)
        var columnNames = records.First().Keys.Take(MaxColumns).ToList();

        // Generate a section block for each record (up to MaxRows)
        var recordsToShow = records.Take(MaxRows).ToList();
        for (int i = 0; i < recordsToShow.Count; i++)
        {
            var record = recordsToShow[i];
            var fields = new List<object>();

            foreach (var colName in columnNames)
            {
                var value = record.TryGetValue(colName, out var val) ? FormatCellValue(val) : string.Empty;
                fields.Add(new
                {
                    type = "mrkdwn",
                    text = $"*{colName}:*\n{value}"
                });
            }

            blocks.Add(new
            {
                type = "section",
                fields
            });

            // Add divider between records for readability
            if (i < recordsToShow.Count - 1)
            {
                blocks.Add(new { type = "divider" });
            }
        }

        return blocks;
    }

    // Keep the old method for reference but rename it
    private object GenerateTableBlock_NotSupportedByWebhooks(List<IDictionary<string, object?>> records)
    {
        if (!records.Any())
        {
            return new { type = "section", text = new { type = "plain_text", text = "No records to display" } };
        }

        // Get all columns (up to MaxColumns)
        var columnNames = records.First().Keys.Take(MaxColumns).ToList();

        // Determine data types for alignment
        var columnTypes = DetermineColumnTypes(records, columnNames);

        // Build column settings
        var columnSettings = columnNames.Select((_, index) =>
        {
            var alignment = GetAlignment(columnTypes[index]);
            return new
            {
                align = alignment,
                is_wrapped = ShouldWrapColumn(columnTypes[index])
            };
        }).ToList();

        // Build header row with bold formatting
        var headerRow = columnNames.Select(colName => new
        {
            type = "rich_text",
            elements = new[]
            {
                new
                {
                    type = "rich_text_section",
                    elements = new[]
                    {
                        new
                        {
                            type = "text",
                            text = colName,
                            style = new { bold = true }
                        }
                    }
                }
            }
        }).ToArray();

        // Build data rows (up to MaxRows)
        var dataRows = records.Take(MaxRows).Select(record =>
        {
            return columnNames.Select(colName =>
            {
                var value = record.TryGetValue(colName, out var val) ? FormatCellValue(val) : string.Empty;
                return new
                {
                    type = "raw_text",
                    text = value
                };
            }).ToArray();
        }).ToList();

        // Combine all rows
        var allRows = new List<object> { headerRow };
        allRows.AddRange(dataRows);

        return new
        {
            type = "table",
            rows = allRows,
            column_settings = columnSettings
        };
    }

    private List<ColumnType> DetermineColumnTypes(List<IDictionary<string, object?>> records, List<string> columnNames)
    {
        var columnTypes = new List<ColumnType>();

        foreach (var columnName in columnNames)
        {
            // Sample first non-null value to determine type
            var sampleValue = records
                .Select(r => r.TryGetValue(columnName, out var val) ? val : null)
                .FirstOrDefault(v => v != null);

            if (sampleValue == null)
            {
                columnTypes.Add(ColumnType.Text);
                continue;
            }

            var type = sampleValue.GetType();

            if (IsNumericType(type))
            {
                columnTypes.Add(ColumnType.Number);
            }
            else if (type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) || type == typeof(TimeOnly))
            {
                columnTypes.Add(ColumnType.Date);
            }
            else if (type == typeof(bool))
            {
                columnTypes.Add(ColumnType.Boolean);
            }
            else
            {
                columnTypes.Add(ColumnType.Text);
            }
        }

        return columnTypes;
    }

    private bool IsNumericType(Type type)
    {
        return type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) ||
               type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte) ||
               type == typeof(float) || type == typeof(double) || type == typeof(decimal);
    }

    private string GetAlignment(ColumnType columnType)
    {
        return columnType switch
        {
            ColumnType.Number => "right",
            ColumnType.Date => "center",
            ColumnType.Boolean => "center",
            _ => "left"
        };
    }

    private bool ShouldWrapColumn(ColumnType columnType)
    {
        // Enable wrapping for text columns that might contain long content
        // Disable for numbers, dates, and booleans which are typically short
        return columnType == ColumnType.Text;
    }

    private string FormatCellValue(object? value)
    {
        if (value == null)
        {
            return string.Empty;
        }

        if (value is DateTime dateTime)
        {
            return dateTime.ToString("yyyy-MM-dd HH:mm:ss");
        }

        if (value is DateTimeOffset dateTimeOffset)
        {
            return dateTimeOffset.ToString("yyyy-MM-dd HH:mm:ss");
        }

        if (value is DateOnly dateOnly)
        {
            return dateOnly.ToString("yyyy-MM-dd");
        }

        if (value is TimeOnly timeOnly)
        {
            return timeOnly.ToString("HH:mm:ss");
        }

        if (value is bool boolean)
        {
            return boolean ? "Yes" : "No";
        }

        return value.ToString() ?? string.Empty;
    }

    private enum ColumnType
    {
        Text,
        Number,
        Date,
        Boolean
    }
}

internal class SlackMessage
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = null!;

    [JsonPropertyName("blocks")]
    public List<object> Blocks { get; set; } = [];
}

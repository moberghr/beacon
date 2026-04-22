using System.Text;
using Beacon.Core.Adapters.Shared;

namespace Beacon.Core.Adapters.Slack;

/// <summary>
/// Formats query results as ASCII-style tables for display in Slack code blocks.
/// </summary>
internal class SlackTableFormatter
{
    private readonly int _maxColumns;
    private readonly int _maxRows;
    private readonly int _maxColumnWidth;
    private readonly int _minColumnWidth;

    public SlackTableFormatter(
        int maxColumns = AdapterConstants.Slack.MaxColumns,
        int maxRows = AdapterConstants.Slack.MaxRows,
        int maxColumnWidth = AdapterConstants.Slack.MaxColumnWidth,
        int minColumnWidth = AdapterConstants.Slack.MinColumnWidth)
    {
        _maxColumns = maxColumns;
        _maxRows = maxRows;
        _maxColumnWidth = maxColumnWidth;
        _minColumnWidth = minColumnWidth;
    }

    /// <summary>
    /// Generates a Slack section block containing an ASCII table in a code block.
    /// </summary>
    public object GenerateTableBlock(List<IDictionary<string, object?>> records)
    {
        if (!records.HasRecords())
        {
            return new { type = "section", text = new { type = "plain_text", text = "No records to display" } };
        }

        // Get column names
        var columnNames = records.GetColumnNamesSafe(_maxColumns);

        // Format all cell values once and cache them (performance optimization)
        var formattedData = PreformatTableData(records, columnNames);

        // Calculate column widths from cached formatted values
        var columnWidths = CalculateColumnWidths(formattedData, columnNames);

        // Build table from cached formatted values
        var tableText = BuildTableText(formattedData, columnNames, columnWidths);

        // Wrap in code block for fixed-width display
        return new
        {
            type = "section",
            text = new
            {
                type = "mrkdwn",
                text = $"```\n{tableText}\n```"
            }
        };
    }

    /// <summary>
    /// Pre-formats all cell values in a single pass to avoid duplicate formatting.
    /// This is a performance optimization that reduces O(2nm) operations to O(nm).
    /// </summary>
    private List<Dictionary<string, string>> PreformatTableData(
        List<IDictionary<string, object?>> records,
        List<string> columnNames)
    {
        var formattedData = new List<Dictionary<string, string>>(Math.Min(records.Count, _maxRows));

        // Single pass through records, formatting each value once
        foreach (var record in records.Take(_maxRows))
        {
            var formattedRow = new Dictionary<string, string>(columnNames.Count);

            foreach (var colName in columnNames)
            {
                var formattedValue = record.TryGetValue(colName, out var val)
                    ? CellValueFormatter.Format(val)
                    : string.Empty;

                formattedRow[colName] = formattedValue;
            }

            formattedData.Add(formattedRow);
        }

        return formattedData;
    }

    /// <summary>
    /// Calculates optimal column widths from pre-formatted data.
    /// </summary>
    private Dictionary<string, int> CalculateColumnWidths(
        List<Dictionary<string, string>> formattedData,
        List<string> columnNames)
    {
        var columnWidths = new Dictionary<string, int>(columnNames.Count);

        // Initialize with column name lengths
        foreach (var colName in columnNames)
        {
            columnWidths[colName] = colName.Length;
        }

        // Single pass through formatted data to find max widths
        foreach (var row in formattedData)
        {
            foreach (var colName in columnNames)
            {
                if (row.TryGetValue(colName, out var formattedValue))
                {
                    columnWidths[colName] = Math.Max(columnWidths[colName], formattedValue.Length);
                }
            }
        }

        // Cap widths at min/max for readability
        foreach (var colName in columnNames)
        {
            columnWidths[colName] = Math.Max(_minColumnWidth, Math.Min(columnWidths[colName], _maxColumnWidth));
        }

        return columnWidths;
    }

    /// <summary>
    /// Builds the final table text from pre-formatted data.
    /// </summary>
    private string BuildTableText(
        List<Dictionary<string, string>> formattedData,
        List<string> columnNames,
        Dictionary<string, int> columnWidths)
    {
        var tableBuilder = new StringBuilder();

        // Header row (centered)
        var headerParts = columnNames.Select(col => PadCenter(col, columnWidths[col]));
        tableBuilder.AppendLine(string.Join(" | ", headerParts));

        // Separator row
        var separatorParts = columnNames.Select(col => new string('-', columnWidths[col]));
        tableBuilder.AppendLine(string.Join("-+-", separatorParts));

        // Data rows (using cached formatted values)
        foreach (var row in formattedData)
        {
            var rowParts = columnNames.Select(col =>
            {
                var value = row.TryGetValue(col, out var formattedValue) ? formattedValue : string.Empty;
                return PadOrTruncate(value, columnWidths[col]);
            });
            tableBuilder.AppendLine(string.Join(" | ", rowParts));
        }

        return tableBuilder.ToString().TrimEnd();
    }

    private string PadOrTruncate(string text, int width)
    {
        if (text.Length > width)
        {
            // Truncate and add ellipsis
            return text.Substring(0, width - 3) + "...";
        }

        // Pad to width (left-aligned)
        return text.PadRight(width);
    }

    private string PadCenter(string text, int width)
    {
        if (text.Length >= width)
        {
            return text.Substring(0, width);
        }

        int totalPadding = width - text.Length;
        int leftPadding = totalPadding / 2;
        int rightPadding = totalPadding - leftPadding;

        return new string(' ', leftPadding) + text + new string(' ', rightPadding);
    }
}

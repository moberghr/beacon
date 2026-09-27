using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Beacon.Core.SavedQueries;
using Beacon.MCP.Tools;

namespace Beacon.MCP.SavedQueries;

/// <summary>
/// The machine-readable result of a saved-query tool call:
/// <c>{ columns: [{name, type}], rows: [{column: value}], rowCount, truncated, queryVersionId, versionNumber }</c>.
/// Held to the same byte budget as the other tools' structured payloads; rows dropped for size set
/// <c>truncated</c> and <c>rowsOmittedForSize</c>.
/// </summary>
internal static class SavedQueryToolResult
{
    public static JsonObject BuildStructured(SavedQueryToolDefinition tool, SavedQueryToolExecution execution)
    {
        var columns = Columns(execution.Rows);
        var rowBudget = execution.Rows.Count;
        var payload = BuildPayload(tool, execution, columns, rowBudget);

        var trimmedForSize = false;
        while (rowBudget > 0 && Encoding.UTF8.GetByteCount(payload.ToJsonString()) > ToolHelper.MaxStructuredPayloadBytes)
        {
            rowBudget /= 2;
            trimmedForSize = true;
            payload = BuildPayload(tool, execution, columns, rowBudget);
        }

        if (trimmedForSize)
        {
            payload["truncated"] = true;
            payload["rowsOmittedForSize"] = true;
        }

        return payload;
    }

    internal static string ColumnType(object? value) =>
        value switch
        {
            null => "null",
            bool => "boolean",
            byte or sbyte or short or ushort or int or uint or long or ulong => "integer",
            float or double or decimal => "number",
            DateTime or DateTimeOffset or DateOnly => "datetime",
            TimeSpan or TimeOnly => "time",
            Guid => "uuid",
            byte[] => "binary",
            _ => "string"
        };

    private static JsonObject BuildPayload(SavedQueryToolDefinition tool, SavedQueryToolExecution execution, List<(string Name, string Type)> columns, int rowBudget)
    {
        var rows = new JsonArray();
        foreach (var row in execution.Rows.Take(rowBudget))
        {
            var item = new JsonObject();
            foreach (var (name, _) in columns)
            {
                item[name] = row.TryGetValue(name, out var value) ? ToNode(value) : null;
            }

            rows.Add(item);
        }

        return new JsonObject
        {
            ["columns"] = new JsonArray(columns
                .Select(x => (JsonNode?)new JsonObject { ["name"] = x.Name, ["type"] = x.Type })
                .ToArray()),
            ["rows"] = rows,
            ["rowCount"] = rows.Count,
            ["truncated"] = execution.Truncated,
            ["queryVersionId"] = tool.QueryVersionId,
            ["versionNumber"] = tool.VersionNumber
        };
    }

    // Column order from the first row; each column's type from its first non-null value.
    private static List<(string Name, string Type)> Columns(IReadOnlyList<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        return rows[0].Keys
            .Select(x => (x, ColumnType(rows
                .Select(y => y.TryGetValue(x, out var value) ? value : null)
                .Where(y => y != null)
                .FirstOrDefault())))
            .ToList();
    }

    private static JsonNode? ToNode(object? value) =>
        value switch
        {
            null => null,
            DBNull => null,
            _ => JsonSerializer.SerializeToNode(value, value.GetType())
        };
}

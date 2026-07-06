using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Path;
using Beacon.Connector.Api.Models;

namespace Beacon.Connector.Api.Services;

public class JsonResponseTabularizer
{
    public List<Dictionary<string, object?>> Tabularize(
        string jsonResponse,
        ApiResultMapping mapping,
        int maxRows = 1000)
    {
        return Tabularize(jsonResponse, mapping, out _, out _, maxRows);
    }

    public List<Dictionary<string, object?>> Tabularize(
        string jsonResponse,
        ApiResultMapping mapping,
        out bool truncated,
        out int totalAvailable,
        int maxRows = 1000)
    {
        truncated = false;
        totalAvailable = 0;

        var rootNode = JsonNode.Parse(jsonResponse);
        if (rootNode == null)
            return new List<Dictionary<string, object?>>();

        var jsonPath = JsonPath.Parse(mapping.ArrayPath);
        var pathResult = jsonPath.Evaluate(rootNode);

        if (pathResult.Matches == null || pathResult.Matches.Count == 0)
            return new List<Dictionary<string, object?>>();

        // Collect all elements from matched arrays/values
        var elements = new List<JsonNode?>();

        foreach (var match in pathResult.Matches)
        {
            if (match.Value is JsonArray array)
            {
                foreach (var item in array)
                    elements.Add(item);
            }
            else
            {
                elements.Add(match.Value);
            }
        }

        if (elements.Count == 0)
            return new List<Dictionary<string, object?>>();

        totalAvailable = elements.Count;

        if (elements.Count > maxRows)
        {
            truncated = true;
            elements = elements.Take(maxRows).ToList();
        }

        if (mapping.Columns != null && mapping.Columns.Count > 0)
            return TabularizeWithExplicitColumns(elements, mapping.Columns);

        return TabularizeAutoDetect(elements);
    }

    private static List<Dictionary<string, object?>> TabularizeWithExplicitColumns(
        List<JsonNode?> elements,
        List<ApiColumnMapping> columns)
    {
        var rows = new List<Dictionary<string, object?>>();

        // Pre-parse each column path once instead of per row.
        var columnPaths = columns
            .Select(col => (Column: col, Path: JsonPath.Parse(col.Path)))
            .ToList();

        foreach (var element in elements)
        {
            if (element == null) continue;

            var row = new Dictionary<string, object?>();

            foreach (var (col, colPath) in columnPaths)
            {
                var colResult = colPath.Evaluate(element);

                if (colResult.Matches != null && colResult.Matches.Count > 0)
                {
                    row[col.Name] = ExtractValue(colResult.Matches[0].Value);
                }
                else
                {
                    row[col.Name] = null;
                }
            }

            rows.Add(row);
        }

        return rows;
    }

    private static List<Dictionary<string, object?>> TabularizeAutoDetect(List<JsonNode?> elements)
    {
        var rows = new List<Dictionary<string, object?>>();

        foreach (var element in elements)
        {
            if (element == null) continue;

            if (element is not JsonObject obj)
            {
                rows.Add(new Dictionary<string, object?> { ["value"] = ExtractValue(element) });
                continue;
            }

            var row = new Dictionary<string, object?>();
            foreach (var property in obj)
            {
                if (property.Value is JsonObject or JsonArray)
                {
                    row[property.Key] = property.Value?.ToJsonString();
                }
                else
                {
                    row[property.Key] = ExtractValue(property.Value);
                }
            }

            rows.Add(row);
        }

        return rows;
    }

    private static object? ExtractValue(JsonNode? node)
    {
        if (node == null) return null;

        // Get the underlying JsonElement to check the value kind
        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue(out string? s)) return s;
            if (jsonValue.TryGetValue(out long l)) return l;
            if (jsonValue.TryGetValue(out double d)) return d;
            if (jsonValue.TryGetValue(out bool b)) return b;
            return jsonValue.ToString();
        }

        return node.ToJsonString();
    }
}

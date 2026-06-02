using System.Text.Json;
using System.Text.RegularExpressions;

namespace Beacon.Core.Adapters.Shared;

/// <summary>
/// Processes templates for any adapter type, replacing placeholders with actual values
/// </summary>
internal static partial class AdapterTemplateProcessor
{
    [GeneratedRegex(@"\{\{([^}]+)\}\}", RegexOptions.Compiled)]
    private static partial Regex PlaceholderRegex();

    /// <summary>
    /// Processes a template string, replacing placeholders with values from the data dictionary
    /// </summary>
    public static string ProcessTemplate(string template, Dictionary<string, object?> templateData)
    {
        var result = template;
        var matches = PlaceholderRegex().Matches(template);

        foreach (Match match in matches)
        {
            var placeholder = match.Groups[1].Value.Trim();
            var value = GetPlaceholderValue(placeholder, templateData);
            result = result.Replace(match.Value, value);
        }

        return result;
    }

    private static string GetPlaceholderValue(string placeholder, Dictionary<string, object?> templateData)
    {
        // Handle nested properties (e.g., Anomaly.IsAnomaly)
        var parts = placeholder.Split('.');
        var key = parts[0];

        // Case-insensitive lookup
        var dataKey = templateData.Keys.FirstOrDefault(k =>
            k.Equals(key, StringComparison.OrdinalIgnoreCase));

        if (dataKey == null)
            return $"{{{{UNKNOWN:{placeholder}}}}}";

        var value = templateData[dataKey];

        // Handle nested properties
        if (parts.Length > 1 && value is Dictionary<string, object?> nestedDict)
        {
            var nestedKey = parts[1];
            var nestedDataKey = nestedDict.Keys.FirstOrDefault(k =>
                k.Equals(nestedKey, StringComparison.OrdinalIgnoreCase));

            if (nestedDataKey != null)
                value = nestedDict[nestedDataKey];
            else
                return $"{{{{UNKNOWN:{placeholder}}}}}";
        }

        return FormatValue(placeholder, value);
    }

    private static string FormatValue(string placeholder, object? value)
    {
        if (value == null)
            return "";

        // Check if placeholder specifically requests pretty formatting
        if (placeholder.EndsWith("_Pretty", StringComparison.OrdinalIgnoreCase))
        {
            return JsonSerializer.Serialize(value, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }

        // For collections and complex objects, serialize as JSON
        if (value is System.Collections.IEnumerable && value is not string)
        {
            return JsonSerializer.Serialize(value, new JsonSerializerOptions
            {
                WriteIndented = false
            });
        }

        // For DateTime, use ISO 8601 format
        if (value is DateTime dt)
            return dt.ToString("o");

        // For booleans, use lowercase
        if (value is bool b)
            return b.ToString().ToLower();

        // For numbers with decimals, format with 2 decimal places
        if (value is double d)
            return d.ToString("F2");

        return value.ToString() ?? "";
    }
}

using System.Text;
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

    private const int MaxOutputLength = 1_048_576;

    /// <summary>
    /// Processes a template string, replacing placeholders with values from the data dictionary. The template is read once:
    /// a substituted value is never searched for placeholders. With <paramref name="escapeJsonStrings"/> a value is escaped
    /// to sit inside a JSON string; the serialized <c>_Pretty</c> and collection placeholders stay raw JSON. With
    /// <paramref name="escapeText"/> every text value (also each string inside a collection) first goes through the
    /// channel's own markup escaping, so a value cannot form a mention, link or tag in the message.
    /// </summary>
    public static string ProcessTemplate(string template, Dictionary<string, object?> templateData, bool escapeJsonStrings = false, Func<string, string>? escapeText = null)
    {
        var result = new StringBuilder(template.Length);
        var position = 0;

        foreach (Match match in PlaceholderRegex().Matches(template))
        {
            result.Append(template, position, match.Index - position);
            position = match.Index + match.Length;

            var placeholder = match.Groups[1].Value.Trim();
            var value = GetPlaceholderValue(placeholder, templateData, escapeJsonStrings, escapeText);
            result.Append(value);

            if (result.Length > MaxOutputLength)
            {
                throw new InvalidOperationException("The expanded template exceeds the size limit.");
            }
        }

        result.Append(template, position, template.Length - position);

        if (result.Length > MaxOutputLength)
        {
            throw new InvalidOperationException("The expanded template exceeds the size limit.");
        }

        return result.ToString();
    }

    private static readonly JsonSerializerOptions EscapeOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string GetPlaceholderValue(string placeholder, Dictionary<string, object?> templateData, bool escapeJsonStrings, Func<string, string>? escapeText)
    {
        // Handle nested properties (e.g., Anomaly.IsAnomaly)
        var parts = placeholder.Split('.');
        var key = parts[0];

        // Case-insensitive lookup
        var dataKey = templateData.Keys.FirstOrDefault(k =>
            k.Equals(key, StringComparison.OrdinalIgnoreCase));

        if (dataKey == null)
            return Unknown(placeholder, escapeJsonStrings, escapeText);

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
                return Unknown(placeholder, escapeJsonStrings, escapeText);
        }

        var serialized = IsSerializedJson(placeholder, value);
        if (escapeText != null && serialized)
        {
            value = EscapeTexts(value, escapeText);
        }

        var formatted = FormatValue(placeholder, value);
        if (escapeText != null && !serialized)
        {
            formatted = escapeText(formatted);
        }

        return escapeJsonStrings && !serialized ? EscapeJsonString(formatted) : formatted;
    }

    private static string Unknown(string placeholder, bool escapeJsonStrings, Func<string, string>? escapeText)
    {
        var text = escapeText == null ? placeholder : escapeText(placeholder);
        var echo = $"{{{{UNKNOWN:{text}}}}}";

        return escapeJsonStrings ? EscapeJsonString(echo) : echo;
    }

    // A copy of the value with every string (and every key) put through the escaping; the caller's data is left as it is.
    private static object? EscapeTexts(object? value, Func<string, string> escapeText)
    {
        switch (value)
        {
            case string text:
                return escapeText(text);
            case IDictionary<string, object?> map:
                var copy = new Dictionary<string, object?>();
                foreach (var x in map)
                {
                    copy[escapeText(x.Key)] = EscapeTexts(x.Value, escapeText);
                }

                return copy;
            case System.Collections.IEnumerable items:
                return items.Cast<object?>().Select(x => EscapeTexts(x, escapeText)).ToList();
            default:
                return value;
        }
    }

    private static bool IsSerializedJson(string placeholder, object? value)
    {
        return value != null
            && (placeholder.EndsWith("_Pretty", StringComparison.OrdinalIgnoreCase) || (value is System.Collections.IEnumerable && value is not string));
    }

    private static string EscapeJsonString(string value)
    {
        var encoded = JsonSerializer.Serialize(value, EscapeOptions);

        return encoded[1..^1];
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

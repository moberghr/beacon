using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Semantico.Core.Helpers;

/// <summary>
/// Extracts readable markdown from documentation content that may contain JSON
/// (either complete or truncated) from multi-agent LLM responses.
/// </summary>
public static partial class DocumentationContentParser
{
    private static readonly HashSet<string> JsonSkipFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "domain_name", "tables_documented", "full_markdown"
    };

    [GeneratedRegex(@"""(\w+)""\s*:\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Singleline)]
    private static partial Regex JsonStringValueRegex();

    /// <summary>
    /// If the content contains JSON (raw or fenced), extracts readable markdown from it.
    /// Returns the original content unchanged if it is not JSON.
    /// </summary>
    public static string ExtractMarkdownFromContent(string content)
    {
        var jsonContent = ExtractJsonContent(content);
        if (jsonContent == null)
            return content;

        // First try proper JSON parsing (works if JSON is complete)
        var parsed = TryParseCompleteJson(jsonContent);
        if (parsed != null)
            return parsed;

        // Fallback: regex-based extraction for truncated JSON
        var extracted = ExtractJsonStringValues(jsonContent);
        return extracted ?? content;
    }

    private static string? ExtractJsonContent(string content)
    {
        var trimmed = content.Trim();

        // Case 1: Content starts directly with JSON
        if (trimmed.StartsWith('{'))
            return trimmed;

        // Case 2: Content contains a ```json code fence (common from LLM multi-agent responses)
        var fenceStart = trimmed.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (fenceStart < 0)
            return null;

        var jsonStart = trimmed.IndexOf('{', fenceStart);
        if (jsonStart < 0)
            return null;

        // Find closing fence after the JSON start
        var fenceEnd = trimmed.IndexOf("\n```", jsonStart);
        return fenceEnd > jsonStart
            ? trimmed.Substring(jsonStart, fenceEnd - jsonStart).Trim()
            : trimmed[jsonStart..].Trim(); // Truncated - take everything
    }

    private static string? TryParseCompleteJson(string json)
    {
        try
        {
            var options = new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            };
            using var doc = JsonDocument.Parse(json, options);

            // Prefer full_markdown if available
            if (doc.RootElement.TryGetProperty("full_markdown", out var fullMd)
                && fullMd.ValueKind == JsonValueKind.String)
            {
                var md = fullMd.GetString();
                if (!string.IsNullOrWhiteSpace(md))
                    return md;
            }

            return BuildMarkdownFromJsonProperties(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? BuildMarkdownFromJsonProperties(JsonElement root)
    {
        var sb = new StringBuilder();

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                continue;
            if (JsonSkipFields.Contains(property.Name))
                continue;

            var value = property.Value.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (!value.TrimStart().StartsWith('#'))
            {
                var header = property.Name.Replace("_", " ");
                header = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(header);
                sb.AppendLine($"## {header}");
                sb.AppendLine();
            }
            sb.AppendLine(value);
            sb.AppendLine();
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static string? ExtractJsonStringValues(string json)
    {
        var matches = JsonStringValueRegex().Matches(json);

        if (matches.Count == 0)
            return null;

        var sb = new StringBuilder();
        string? fullMarkdown = null;

        foreach (Match match in matches)
        {
            var key = match.Groups[1].Value;
            var rawValue = match.Groups[2].Value;

            // Unescape JSON string escapes (order matters: \\ must be last to avoid double-unescaping)
            var value = rawValue
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t")
                .Replace("\\/", "/")
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\");

            if (key.Equals("full_markdown", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
            {
                fullMarkdown = value;
                continue;
            }

            if (JsonSkipFields.Contains(key) || string.IsNullOrWhiteSpace(value))
                continue;

            // If the value already starts with a markdown header, don't add another
            if (!value.TrimStart().StartsWith('#'))
            {
                var header = key.Replace("_", " ");
                header = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(header);
                sb.AppendLine($"## {header}");
                sb.AppendLine();
            }
            sb.AppendLine(value);
            sb.AppendLine();
        }

        // Prefer full_markdown if found
        if (!string.IsNullOrWhiteSpace(fullMarkdown))
            return fullMarkdown;

        return sb.Length > 0 ? sb.ToString() : null;
    }
}

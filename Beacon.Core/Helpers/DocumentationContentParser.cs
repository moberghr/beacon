using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Beacon.Core.Helpers;

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

    [GeneratedRegex(@"```mermaid\s*\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex MermaidBlockRegex();

    /// <summary>
    /// Finds all ```mermaid blocks in markdown content and fixes common syntax issues
    /// caused by LLM output truncation (unclosed braces, unclosed quotes, incomplete lines).
    /// </summary>
    public static string SanitizeMermaidDiagrams(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        return MermaidBlockRegex().Replace(content, match =>
        {
            var diagram = match.Groups[1].Value;
            var sanitized = SanitizeSingleDiagram(diagram);

            return $"```mermaid\n{sanitized}```";
        });
    }

    private static string SanitizeSingleDiagram(string diagram)
    {
        var lines = diagram.Split('\n').ToList();

        // Remove trailing incomplete lines (lines that end mid-token or mid-string)
        while (lines.Count > 0)
        {
            var lastLine = lines[^1].TrimEnd();
            if (string.IsNullOrWhiteSpace(lastLine))
            {
                lines.RemoveAt(lines.Count - 1);
                continue;
            }

            // Check for incomplete quoted strings (odd number of unescaped quotes)
            var quoteCount = lastLine.Count(c => c == '"');
            if (quoteCount % 2 != 0)
            {
                lines.RemoveAt(lines.Count - 1);
                continue;
            }

            break;
        }

        // Balance braces — close any unclosed { blocks
        var openBraces = 0;
        foreach (var line in lines)
        {
            openBraces += line.Count(c => c == '{');
            openBraces -= line.Count(c => c == '}');
        }

        while (openBraces > 0)
        {
            lines.Add("    }");
            openBraces--;
        }

        // Ensure diagram ends with a newline
        if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.Add("");
        }

        return string.Join('\n', lines);
    }

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

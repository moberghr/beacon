using System.Text.Json;
using System.Text.Json.Nodes;

namespace Beacon.Core.HostDocs;

internal sealed record ParsedHostDocument(string Title, string Body, string? FrontmatterJson);

/// <summary>
/// Splits a document into YAML frontmatter and body and picks its title. The frontmatter reader understands the
/// flat subset documentation generators emit — <c>key: value</c> scalars, quoted strings, inline <c>[a, b]</c> lists
/// and block <c>- item</c> lists; nested maps are skipped. Title: frontmatter <c>title</c>, else the first
/// <c># </c> heading outside a code fence, else the file name.
/// </summary>
internal static class HostDocumentParser
{
    public const int MaxTitleLength = 500;

    public static ParsedHostDocument Parse(string path, string rawContent)
    {
        var text = rawContent.Replace("\r\n", "\n").TrimStart('﻿');
        var (frontmatter, body) = SplitFrontmatter(text);

        var title = frontmatter?["title"] is JsonValue value && value.TryGetValue<string>(out var fromFrontmatter)
            ? fromFrontmatter
            : null;

        if (string.IsNullOrWhiteSpace(title))
        {
            title = FirstHeading(body) ?? Path.GetFileNameWithoutExtension(path);
        }

        title = title.Trim();
        if (title.Length > MaxTitleLength)
        {
            title = title[..MaxTitleLength];
        }

        var json = frontmatter is { Count: > 0 } ? frontmatter.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) : null;

        return new ParsedHostDocument(title, body, json);
    }

    private static (JsonObject? Frontmatter, string Body) SplitFrontmatter(string text)
    {
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
        {
            return (null, text);
        }

        var lines = text.Split('\n');
        var end = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimEnd();
            if (trimmed is "---" or "...")
            {
                end = i;
                break;
            }
        }

        // An unterminated block is not frontmatter — keep the document as written.
        if (end < 0)
        {
            return (null, text);
        }

        var frontmatter = ParseYamlSubset(lines[1..end]);
        var body = string.Join('\n', lines[(end + 1)..]).TrimStart('\n');

        return (frontmatter, body);
    }

    private static JsonObject ParseYamlSubset(string[] lines)
    {
        var result = new JsonObject();
        string? listKey = null;
        JsonArray? list = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var indented = char.IsWhiteSpace(line[0]);
            var trimmed = line.Trim();

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed == "-")
            {
                if (list != null)
                {
                    list.Add(Unquote(trimmed.Length > 1 ? trimmed[2..].Trim() : string.Empty));
                }

                continue;
            }

            if (indented)
            {
                // Nested map content under a key — outside the supported subset.
                continue;
            }

            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = trimmed[..colon].Trim();
            var value = trimmed[(colon + 1)..].Trim();
            CloseEmptyList(result, listKey, list);

            if (value.Length == 0)
            {
                list = [];
                listKey = key;
                result[key] = list;
                continue;
            }

            list = null;
            listKey = null;
            result[key] = value.StartsWith('[') && value.EndsWith(']')
                ? InlineList(value)
                : JsonValue.Create(Unquote(value));
        }

        CloseEmptyList(result, listKey, list);

        return result;
    }

    // A bare "key:" with no list items is an empty value, not an empty list.
    private static void CloseEmptyList(JsonObject result, string? listKey, JsonArray? list)
    {
        if (listKey != null && list is { Count: 0 })
        {
            result[listKey] = null;
        }
    }

    private static JsonArray InlineList(string value)
    {
        var items = value[1..^1]
            .Split(',')
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Select(x => (JsonNode?)JsonValue.Create(Unquote(x)))
            .ToArray();

        return new JsonArray(items);
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static string? FirstHeading(string body)
    {
        var inFence = false;
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence && trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                var heading = trimmed[2..].Trim();
                if (heading.Length > 0)
                {
                    return heading;
                }
            }
        }

        return null;
    }
}

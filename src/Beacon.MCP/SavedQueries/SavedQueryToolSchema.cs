using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Beacon.Core.Data.Enums;
using Beacon.Core.SavedQueries;

namespace Beacon.MCP.SavedQueries;

/// <summary>
/// The MCP face of a saved-query tool: its input schema (built from the approved version's parameters) and the
/// conversion of incoming JSON arguments to typed values. Every parameter is required — the query model has no
/// defaults — and unknown arguments are rejected.
/// </summary>
internal static class SavedQueryToolSchema
{
    public const int MaxStringArgumentLength = 4000;

    /// <param name="includeProject">
    /// True when the caller reaches the tool through several projects, so the schema offers <c>project_id</c> to pick one.
    /// </param>
    public static JsonObject BuildInputSchema(SavedQueryToolDefinition tool, bool includeProject)
    {
        var properties = new JsonObject();
        foreach (var parameter in tool.Parameters)
        {
            properties[parameter.Name] = ParameterSchema(parameter);
        }

        if (includeProject)
        {
            properties[SavedQueryToolRules.ProjectArgumentName] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = $"The project to run in; one of {string.Join(", ", tool.ProjectIds)}. Required because your credentials reach this tool through several projects.",
                ["enum"] = new JsonArray(tool.ProjectIds.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray())
            };
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false
        };

        if (tool.Parameters.Count > 0)
        {
            schema["required"] = new JsonArray(tool.Parameters.Select(x => (JsonNode?)JsonValue.Create(x.Name)).ToArray());
        }

        return schema;
    }

    public static Tool ToProtocolTool(SavedQueryToolDefinition tool, bool includeProject) =>
        new()
        {
            Name = tool.ToolName,
            Title = tool.Title,
            Description = Describe(tool),
            InputSchema = JsonSerializer.SerializeToElement(BuildInputSchema(tool, includeProject)),
            Annotations = new ToolAnnotations
            {
                Title = tool.Title,
                ReadOnlyHint = true,
                DestructiveHint = false,
                IdempotentHint = true,
                OpenWorldHint = false
            }
        };

    public static string Describe(SavedQueryToolDefinition tool) =>
        $"{tool.Description}\n\nSaved Beacon query (approved version {tool.VersionNumber}); runs read-only with the arguments bound as database parameters. Results are row-capped and PII-masked per the project's MCP settings.";

    /// <summary>
    /// Reads only the optional <c>project_id</c> argument, so the project can be resolved (and its retention settings
    /// applied to the audit row) before the other arguments are validated. Returns an error message, or null with
    /// <paramref name="projectId"/> set when a project was passed.
    /// </summary>
    public static string? ReadProjectArgument(IDictionary<string, JsonElement>? arguments, out int? projectId)
    {
        projectId = null;
        if (arguments == null
            || !arguments.TryGetValue(SavedQueryToolRules.ProjectArgumentName, out var element)
            || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (!TryGetInteger(element, out var requested) || requested is < int.MinValue or > int.MaxValue)
        {
            return $"Argument '{SavedQueryToolRules.ProjectArgumentName}' must be an integer project id.";
        }

        projectId = (int)requested;

        return null;
    }

    /// <summary>
    /// Validates and converts <paramref name="arguments"/> against the tool's parameters. Returns an error message, or
    /// null with <paramref name="values"/> keyed by parameter name and <paramref name="projectId"/> set when the
    /// caller passed <c>project_id</c>.
    /// </summary>
    public static string? ConvertArguments(
        SavedQueryToolDefinition tool,
        IDictionary<string, JsonElement>? arguments,
        out Dictionary<string, object?> values,
        out int? projectId)
    {
        values = new Dictionary<string, object?>(StringComparer.Ordinal);
        projectId = null;
        var supplied = arguments ?? new Dictionary<string, JsonElement>();

        foreach (var (name, element) in supplied)
        {
            if (name == SavedQueryToolRules.ProjectArgumentName)
            {
                if (element.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (!TryGetInteger(element, out var requested) || requested is < int.MinValue or > int.MaxValue)
                {
                    return $"Argument '{SavedQueryToolRules.ProjectArgumentName}' must be an integer project id.";
                }

                projectId = (int)requested;
                continue;
            }

            if (!tool.Parameters.Any(x => x.Name == name))
            {
                var expected = tool.Parameters.Count == 0 ? "none" : string.Join(", ", tool.Parameters.Select(x => x.Name));
                return $"Unknown argument '{name}'. Expected: {expected}.";
            }
        }

        foreach (var parameter in tool.Parameters)
        {
            if (!supplied.TryGetValue(parameter.Name, out var element) || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return $"Missing required argument '{parameter.Name}' ({TypeLabel(parameter.Type)}).";
            }

            var error = Convert(parameter, element, out var value);
            if (error != null)
            {
                return error;
            }

            values[parameter.Name] = value;
        }

        return null;
    }

    private static string? Convert(SavedQueryToolParameter parameter, JsonElement element, out object? value)
    {
        value = null;

        switch (parameter.Type)
        {
            case ParameterType.Number:
                if (TryGetInteger(element, out var integer))
                {
                    value = integer;
                    return null;
                }

                if (TryGetDecimal(element, out var number))
                {
                    value = number;
                    return null;
                }

                return $"Argument '{parameter.Name}' must be a number.";

            case ParameterType.DateTime:
                if (element.ValueKind == JsonValueKind.String && TryParseDateTime(element.GetString()!, out var dateTime))
                {
                    value = dateTime;
                    return null;
                }

                return $"Argument '{parameter.Name}' must be an ISO 8601 date or date-time string (for example 2026-01-31 or 2026-01-31T00:00:00Z).";

            default:
                if (element.ValueKind != JsonValueKind.String)
                {
                    return $"Argument '{parameter.Name}' must be a string.";
                }

                var text = element.GetString()!;
                if (text.Length > MaxStringArgumentLength)
                {
                    return $"Argument '{parameter.Name}' is longer than {MaxStringArgumentLength} characters.";
                }

                value = text;
                return null;
        }
    }

    private static JsonObject ParameterSchema(SavedQueryToolParameter parameter)
    {
        var description = string.IsNullOrWhiteSpace(parameter.Description) ? null : parameter.Description.Trim();

        return parameter.Type switch
        {
            ParameterType.Number => new JsonObject
            {
                ["type"] = "number",
                ["description"] = description ?? parameter.Name
            },
            ParameterType.DateTime => new JsonObject
            {
                ["type"] = "string",
                ["description"] = $"{description ?? parameter.Name} — ISO 8601 date or date-time (for example 2026-01-31 or 2026-01-31T00:00:00Z)."
            },
            _ => new JsonObject
            {
                ["type"] = "string",
                ["maxLength"] = MaxStringArgumentLength,
                ["description"] = description ?? parameter.Name
            }
        };
    }

    private static string TypeLabel(ParameterType type) =>
        type switch
        {
            ParameterType.Number => "number",
            ParameterType.DateTime => "ISO 8601 date-time",
            _ => "string"
        };

    // Numbers arrive as JSON numbers; a numeric string is accepted too, since some clients stringify every argument.
    private static bool TryGetInteger(JsonElement element, out long value)
    {
        value = 0;

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(element.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }

    private static bool TryGetDecimal(JsonElement element, out decimal value)
    {
        value = 0;

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDecimal(out value),
            JsonValueKind.String => decimal.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }

    // An explicit offset or Z is honored and normalized to UTC; a bare date or local date-time stays unspecified, so
    // it compares with timestamp-without-time-zone columns as written.
    private static bool TryParseDateTime(string text, out DateTime value)
    {
        value = default;
        var hasOffset = text.EndsWith('Z') || text.EndsWith('z') || HasNumericOffset(text);

        if (hasOffset)
        {
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset))
            {
                return false;
            }

            value = offset.UtcDateTime;
            return true;
        }

        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return false;
        }

        value = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return true;
    }

    private static bool HasNumericOffset(string text)
    {
        var timeIndex = text.IndexOf('T');
        if (timeIndex < 0)
        {
            return false;
        }

        var time = text[(timeIndex + 1)..];

        return time.Contains('+') || time.Contains('-');
    }
}

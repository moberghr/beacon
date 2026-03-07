using System.Text.Json;
using Semantico.MCP.Protocol;

namespace Semantico.MCP.Tools;

internal static class ToolHelper
{
    public static McpToolResult TextResult(string text) => new()
    {
        Content = [new McpContent { Text = text }]
    };

    public static McpToolResult ErrorResult(string error) => new()
    {
        Content = [new McpContent { Text = error }],
        IsError = true
    };

    public static T? GetParam<T>(JsonElement? args, string name)
    {
        if (args == null) return default;
        if (args.Value.TryGetProperty(name, out var prop))
            return JsonSerializer.Deserialize<T>(prop.GetRawText());
        return default;
    }

    public static string? GetString(JsonElement? args, string name)
    {
        if (args == null) return null;
        if (args.Value.TryGetProperty(name, out var prop))
            return prop.GetString();
        return null;
    }

    public static int? GetInt(JsonElement? args, string name)
    {
        if (args == null) return null;
        if (args.Value.TryGetProperty(name, out var prop) && prop.TryGetInt32(out var val))
            return val;
        return null;
    }

    public static bool GetBool(JsonElement? args, string name, bool defaultValue = false)
    {
        if (args == null) return defaultValue;
        if (args.Value.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.True)
            return true;
        if (args.Value.TryGetProperty(name, out prop) && prop.ValueKind == JsonValueKind.False)
            return false;
        return defaultValue;
    }

    public static object SchemaObject(Dictionary<string, object> properties, string[]? required = null)
    {
        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties
        };
        if (required != null)
            schema["required"] = required;
        return schema;
    }

    public static object StringProp(string description) => new Dictionary<string, object>
    {
        ["type"] = "string",
        ["description"] = description
    };

    public static object IntProp(string description) => new Dictionary<string, object>
    {
        ["type"] = "integer",
        ["description"] = description
    };

    public static object BoolProp(string description) => new Dictionary<string, object>
    {
        ["type"] = "boolean",
        ["description"] = description
    };

    /// <summary>
    /// Returns an error result if the session's API key restricts access to specific datasources
    /// and the requested datasource is not in the allowed list. Returns null if access is allowed.
    /// </summary>
    public static McpToolResult? ValidateDataSourceAccess(McpClientSession session, int dataSourceId)
    {
        if (session.AllowedDataSourceIds != null && !session.AllowedDataSourceIds.Contains(dataSourceId))
            return ErrorResult($"Access denied: your API key does not have access to data source {dataSourceId}.");
        return null;
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
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
    /// Resolves the active project ID from the session. If the session has exactly one allowed project,
    /// it auto-resolves. If multiple, the caller must provide a project_id parameter.
    /// Returns null on success (with projectId set), or an error result.
    /// </summary>
    public static McpToolResult? ResolveProjectId(McpClientSession session, int? requestedProjectId, out int projectId)
    {
        projectId = 0;

        // If session already has an active project and no override requested, use it
        if (session.ActiveProjectId.HasValue && requestedProjectId == null)
        {
            projectId = session.ActiveProjectId.Value;
            return null;
        }

        if (requestedProjectId.HasValue)
        {
            // Validate the requested project is in the allowed list
            if (session.AllowedProjectIds != null && !session.AllowedProjectIds.Contains(requestedProjectId.Value))
                return ErrorResult($"Access denied: your API key does not have access to project {requestedProjectId.Value}.");

            projectId = requestedProjectId.Value;
            session.ActiveProjectId = projectId;
            return null;
        }

        // No project requested — try auto-resolve
        if (session.AllowedProjectIds == null || session.AllowedProjectIds.Count == 0)
            return ErrorResult("No projects are associated with this API key. Create a project and regenerate your API key with project access.");

        if (session.AllowedProjectIds.Count == 1)
        {
            projectId = session.AllowedProjectIds[0];
            session.ActiveProjectId = projectId;
            return null;
        }

        // Multiple projects — need disambiguation
        return ErrorResult($"Multiple projects available (IDs: {string.Join(", ", session.AllowedProjectIds)}). Specify project_id parameter to select one.");
    }

    /// <summary>
    /// Validates that a data source belongs to the given project.
    /// </summary>
    public static async Task<McpToolResult?> ValidateDataSourceInProjectAsync(
        IDbContextFactory<SemanticoContext> contextFactory, int projectId, int dataSourceId, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var belongs = await context.ProjectDataSources
            .AnyAsync(pds => pds.ProjectId == projectId && pds.DataSourceId == dataSourceId, ct);

        if (!belongs)
            return ErrorResult($"Data source {dataSourceId} is not part of project {projectId}.");

        return null;
    }

    /// <summary>
    /// Gets all data source IDs belonging to a project.
    /// </summary>
    public static async Task<List<int>> GetProjectDataSourceIdsAsync(
        IDbContextFactory<SemanticoContext> contextFactory, int projectId, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        return await context.ProjectDataSources
            .Where(pds => pds.ProjectId == projectId)
            .Select(pds => pds.DataSourceId)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Resolves a data source by name within a project.
    /// </summary>
    public static async Task<(int DataSourceId, McpToolResult? Error)> ResolveDataSourceByNameAsync(
        IDbContextFactory<SemanticoContext> contextFactory, int projectId, string dataSourceName, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var ds = await context.ProjectDataSources
            .Where(pds => pds.ProjectId == projectId)
            .Where(pds => pds.DataSource.Name.ToLower() == dataSourceName.ToLower())
            .Select(pds => new { pds.DataSourceId })
            .FirstOrDefaultAsync(ct);

        if (ds == null)
            return (0, ErrorResult($"Data source '{dataSourceName}' not found in this project."));

        return (ds.DataSourceId, null);
    }

    /// <summary>
    /// Formats query results as a markdown table.
    /// </summary>
    public static string FormatResultsAsMarkdown(IReadOnlyList<Dictionary<string, object?>> rows, int maxRows = 100)
    {
        return FormatResultsAsMarkdownInternal(rows, maxRows);
    }

    public static string FormatResultsAsMarkdown(List<IDictionary<string, object?>> rows, int maxRows = 100)
    {
        return FormatResultsAsMarkdownInternal(rows, maxRows);
    }

    private static string FormatResultsAsMarkdownInternal<T>(IReadOnlyList<T> rows, int maxRows) where T : IDictionary<string, object?>
    {
        if (rows.Count == 0) return "No results returned.\n";

        var columns = rows[0].Keys.ToList();
        var text = "| " + string.Join(" | ", columns) + " |\n";
        text += "| " + string.Join(" | ", columns.Select(_ => "---")) + " |\n";

        foreach (var row in rows.Take(maxRows))
        {
            text += "| " + string.Join(" | ", columns.Select(c =>
                row.TryGetValue(c, out var v) ? (v?.ToString() ?? "NULL") : "NULL")) + " |\n";
        }

        return text;
    }
}

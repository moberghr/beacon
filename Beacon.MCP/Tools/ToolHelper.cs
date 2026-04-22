using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using Beacon.Core.Data;
using Beacon.MCP.Services;

namespace Beacon.MCP.Tools;

internal static class ToolHelper
{
    /// <summary>
    /// Resolves the active project ID from context + session manager.
    /// Returns null on success (with projectId set), or an error message string.
    /// </summary>
    public static string? ResolveProjectId(
        IProjectContext context,
        McpProjectContextManager sessionManager,
        int? requestedProjectId,
        out int projectId)
    {
        projectId = 0;
        var key = McpProjectContextManager.MakeKey(context.UserId, context.ApiKeyId);
        var state = sessionManager.GetOrCreate(key);

        // If session already has an active project and no override requested, use it
        if (state.ActiveProjectId.HasValue && requestedProjectId == null)
        {
            projectId = state.ActiveProjectId.Value;
            context.ActiveProjectId = projectId;
            return null;
        }

        if (requestedProjectId.HasValue)
        {
            if (context.AllowedProjectIds != null && !context.AllowedProjectIds.Contains(requestedProjectId.Value))
                return $"Access denied: your API key does not have access to project {requestedProjectId.Value}.";

            projectId = requestedProjectId.Value;
            state.ActiveProjectId = projectId;
            context.ActiveProjectId = projectId;
            return null;
        }

        // No project requested — try auto-resolve
        if (context.AllowedProjectIds == null || context.AllowedProjectIds.Count == 0)
            return "No projects are associated with this API key. Create a project and regenerate your API key with project access.";

        if (context.AllowedProjectIds.Count == 1)
        {
            projectId = context.AllowedProjectIds[0];
            state.ActiveProjectId = projectId;
            context.ActiveProjectId = projectId;
            return null;
        }

        return $"Multiple projects available (IDs: {string.Join(", ", context.AllowedProjectIds)}). Specify project_id parameter to select one.";
    }

    /// <summary>
    /// Validates that a data source belongs to the given project. Returns an error string or null.
    /// </summary>
    public static async Task<string?> ValidateDataSourceInProjectAsync(
        IDbContextFactory<BeaconContext> contextFactory, int projectId, int dataSourceId, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var belongs = await context.ProjectDataSources
            .AnyAsync(pds => pds.ProjectId == projectId && pds.DataSourceId == dataSourceId, ct);

        return belongs ? null : $"Data source {dataSourceId} is not part of project {projectId}.";
    }

    /// <summary>
    /// Gets all data source IDs belonging to a project.
    /// </summary>
    public static async Task<List<int>> GetProjectDataSourceIdsAsync(
        IDbContextFactory<BeaconContext> contextFactory, int projectId, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        return await context.ProjectDataSources
            .Where(pds => pds.ProjectId == projectId)
            .Select(pds => pds.DataSourceId)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Resolves a data source by name within a project. Returns (id, error) where error is null on success.
    /// </summary>
    public static async Task<(int DataSourceId, string? Error)> ResolveDataSourceByNameAsync(
        IDbContextFactory<BeaconContext> contextFactory, int projectId, string dataSourceName, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var ds = await context.ProjectDataSources
            .Where(pds => pds.ProjectId == projectId)
            .Where(pds => pds.DataSource.Name.ToLower() == dataSourceName.ToLower())
            .Select(pds => new { pds.DataSourceId })
            .FirstOrDefaultAsync(ct);

        if (ds == null)
            return (0, $"Data source '{dataSourceName}' not found in this project.");

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

    public static CallToolResult Success(string text) =>
        new() { Content = [new TextContentBlock { Text = text }] };

    public static CallToolResult Error(string message) =>
        new() { Content = [new TextContentBlock { Text = message }], IsError = true };

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

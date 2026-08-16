using System.Collections.Generic;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using Beacon.Core.Data;
using Beacon.Core.Models;
using Beacon.MCP.Services;

namespace Beacon.MCP.Tools;

internal static class ToolHelper
{
    /// <summary>
    /// Resolves the active project ID as a pure per-call function of the request-scoped context
    /// and the explicit project_id parameter — no cross-call state, so any instance behind a load
    /// balancer resolves identically. Returns null on success (with projectId set), or an error
    /// message string.
    /// </summary>
    public static string? ResolveProjectId(
        IProjectContext context,
        int? requestedProjectId,
        out int projectId)
    {
        projectId = 0;

        if (requestedProjectId.HasValue)
        {
            // A null AllowedProjectIds means unrestricted; an empty list denies everything (fail closed).
            if (context.AllowedProjectIds != null && !context.AllowedProjectIds.Contains(requestedProjectId.Value))
            {
                return $"Access denied: your API key does not have access to project {requestedProjectId.Value}.";
            }

            projectId = requestedProjectId.Value;
            context.ActiveProjectId = projectId;
            return null;
        }

        // No project requested — try auto-resolve
        if (context.AllowedProjectIds == null || context.AllowedProjectIds.Count == 0)
        {
            return "No projects are associated with this API key. Create a project and regenerate your API key with project access.";
        }

        if (context.AllowedProjectIds.Count == 1)
        {
            projectId = context.AllowedProjectIds[0];
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

    /// <summary>
    /// Builds the machine-readable companion to <see cref="FormatResultsAsMarkdown(IReadOnlyList{Dictionary{string, object?}}, int)"/>:
    /// { "columns": [..], "rows": [[..], ..], "row_count": N, "truncated": bool }. The rows array honors the
    /// same maxRows budget as the markdown table; row_count is the total row count received, and truncated is
    /// true whenever a truncation notice would appear in the markdown (rows.Count >= maxRows).
    /// </summary>
    public static JsonNode BuildStructuredPayload(IReadOnlyList<Dictionary<string, object?>> rows, int maxRows = 100)
    {
        return BuildStructuredPayloadInternal(rows, maxRows);
    }

    public static JsonNode BuildStructuredPayload(List<IDictionary<string, object?>> rows, int maxRows = 100)
    {
        return BuildStructuredPayloadInternal(rows, maxRows);
    }

    /// <summary>
    /// Maps an exception to a message safe to return to the MCP caller. Business-rule and domain
    /// exceptions (§2.9) are written for the caller and pass through; database provider errors are
    /// surfaced so an agent can correct its own SQL; anything else stays internal — full detail
    /// goes to the audit trail and server log, never the wire.
    /// </summary>
    public static string CallerSafeMessage(Exception ex, string tool) =>
        ex switch
        {
            InvalidOperationException or BeaconException => ex.Message,
            DbException => $"Query failed: {ex.Message}",
            _ => $"The {tool} tool failed due to an unexpected internal error. Retry the call; if the problem persists, ask your Beacon administrator to check the audit log."
        };

    public static CallToolResult Success(string text) =>
        Success(text, null);

    // SDK 2.2's CallToolResult.StructuredContent is a JsonElement?, so the JsonNode payload the
    // tools build is converted at this single boundary; null structured leaves the field unset.
    public static CallToolResult Success(string text, JsonNode? structured) =>
        new()
        {
            Content = [new TextContentBlock { Text = text }],
            StructuredContent = structured == null ? null : JsonSerializer.SerializeToElement(structured)
        };

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

        // Result-budget honesty: never silently drop rows. rows.Count == maxRows most likely means
        // the SQL-level row cap was hit, so the full result set may be larger than what came back.
        if (rows.Count > maxRows)
        {
            text += $"\n_Showing {maxRows} of {rows.Count} rows (truncated). Narrow the query or raise max_rows._\n";
        }
        else if (rows.Count == maxRows)
        {
            text += $"\n_Row cap of {maxRows} reached — the result set may be truncated._\n";
        }

        return text;
    }

    private static JsonNode BuildStructuredPayloadInternal<T>(IReadOnlyList<T> rows, int maxRows) where T : IDictionary<string, object?>
    {
        var columns = rows.Count > 0
            ? rows[0].Keys.ToList()
            : new List<string>();

        var rowsNode = new JsonArray();
        foreach (var row in rows.Take(maxRows))
        {
            var rowNode = new JsonArray();
            foreach (var column in columns)
            {
                rowNode.Add(row.TryGetValue(column, out var value) ? ToJsonValue(value) : null);
            }

            rowsNode.Add(rowNode);
        }

        return new JsonObject
        {
            ["columns"] = new JsonArray(columns.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["rows"] = rowsNode,
            ["row_count"] = rows.Count,
            ["truncated"] = rows.Count > 0 && rows.Count >= maxRows
        };
    }

    private static JsonNode? ToJsonValue(object? value)
    {
        return value switch
        {
            null => null,
            bool x => JsonValue.Create(x),
            string x => JsonValue.Create(x),
            byte x => JsonValue.Create(x),
            sbyte x => JsonValue.Create(x),
            short x => JsonValue.Create(x),
            ushort x => JsonValue.Create(x),
            int x => JsonValue.Create(x),
            uint x => JsonValue.Create(x),
            long x => JsonValue.Create(x),
            ulong x => JsonValue.Create(x),
            float x => JsonValue.Create(x),
            double x => JsonValue.Create(x),
            decimal x => JsonValue.Create(x),
            _ => JsonValue.Create(value.ToString())
        };
    }
}

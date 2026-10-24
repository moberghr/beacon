using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Mcp;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.MCP.Services;

namespace Beacon.MCP.Tools;

internal static class ToolHelper
{
    // Default byte budget for a SQL result (256 KB) — the MaxResultBytes default of the MCP settings. Callers pass the
    // resolved McpSettingsData.MaxResultBytes; the default applies only where no settings are at hand. One tool result
    // carries its rows twice (the markdown text and the structured payload), so a result rendered both ways splits the
    // budget between them (FormatResultWithinBudget, StructuredShare): an unbounded rows array with wide values can blow
    // the response past what MCP clients accept even when the row COUNT is within maxRows.
    internal const int MaxStructuredPayloadBytes = 262144;

    private const string SizeBudgetNotice = "\n_Further rows omitted — response size budget reached._\n";

    /// <summary>
    /// Resolves the active project ID as a pure per-call function of the request-scoped context
    /// and the explicit project_id parameter — no cross-call state, so any instance behind a load
    /// balancer resolves identically. Returns null on success (with projectId set), or an error
    /// message string. Only a project in <see cref="IProjectContext.AllowedProjectIds"/> resolves: a
    /// null list (no caller resolved) reaches no project, like an empty one (fail closed). A tool that
    /// works ad hoc in the project (<see cref="McpToolProjectAction"/>: the SQL-carrying tools) needs
    /// the project in <see cref="IProjectContext.QueryProjectIds"/> as well, so a caller who may only
    /// read it (a Viewer) reads the catalog but runs no SQL there.
    /// </summary>
    public static string? ResolveProjectId(
        IProjectContext context,
        string toolName,
        int? requestedProjectId,
        out int projectId)
    {
        var error = ResolveReadableProjectId(context, requestedProjectId, out projectId);
        if (error != null)
        {
            return error;
        }

        if (McpToolProjectAction.Of(toolName) != ProjectAction.Query || context.QueryProjectIds?.Contains(projectId) == true)
        {
            return null;
        }

        var refused = projectId;
        projectId = 0;
        context.ActiveProjectId = null;

        return $"Access denied: you may read project {refused} but not run ad-hoc queries in it.";
    }

    /// <summary>
    /// Validates that a data source belongs to the given project. Returns an error string or null.
    /// </summary>
    public static async Task<string?> ValidateDataSourceInProjectAsync(
        IDbContextFactory<BeaconContext> contextFactory, int projectId, int dataSourceId, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var belongs = await context.ProjectDataSources
            .WhereReadable()
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
            .WhereReadable()
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
            .WhereReadable()
            .Where(pds => pds.ProjectId == projectId)
            .Where(pds => pds.DataSource.Name.ToLower() == dataSourceName.ToLower())
            .Select(pds => new { pds.DataSourceId })
            .FirstOrDefaultAsync(ct);

        if (ds == null)
            return (0, $"Data source '{dataSourceName}' not found in this project.");

        return (ds.DataSourceId, null);
    }

    /// <summary>
    /// Formats query results as a markdown table of at most <paramref name="maxRows"/> rows and
    /// <paramref name="maxResultBytes"/> UTF-8 bytes (the resolved <c>MaxResultBytes</c> setting); rows past the size
    /// budget are omitted with a note saying so.
    /// </summary>
    public static string FormatResultsAsMarkdown(IReadOnlyList<Dictionary<string, object?>> rows, int maxRows = 100, int maxResultBytes = MaxStructuredPayloadBytes)
    {
        return FormatResultsAsMarkdownInternal(rows, maxRows, maxResultBytes);
    }

    public static string FormatResultsAsMarkdown(List<IDictionary<string, object?>> rows, int maxRows = 100, int maxResultBytes = MaxStructuredPayloadBytes)
    {
        return FormatResultsAsMarkdownInternal(rows, maxRows, maxResultBytes);
    }

    /// <summary>
    /// Builds the machine-readable companion to <see cref="FormatResultsAsMarkdown(IReadOnlyList{Dictionary{string, object?}}, int, int)"/>:
    /// { "columns": [..], "rows": [[..], ..], "row_count": N, "truncated": bool }. The rows array honors the
    /// same maxRows budget as the markdown table; row_count is the total row count received, and truncated is
    /// true whenever a truncation notice would appear in the markdown (rows.Count >= maxRows). When the serialized
    /// payload would exceed <paramref name="maxResultBytes"/> it carries fewer rows, with truncated and
    /// rows_omitted_for_size set.
    /// </summary>
    public static JsonNode BuildStructuredPayload(IReadOnlyList<Dictionary<string, object?>> rows, int maxRows = 100, int maxResultBytes = MaxStructuredPayloadBytes)
    {
        return BuildStructuredPayloadInternal(rows, maxRows, maxResultBytes);
    }

    public static JsonNode BuildStructuredPayload(List<IDictionary<string, object?>> rows, int maxRows = 100, int maxResultBytes = MaxStructuredPayloadBytes)
    {
        return BuildStructuredPayloadInternal(rows, maxRows, maxResultBytes);
    }

    /// <summary>
    /// The part of one tool result's <c>MaxResultBytes</c> its structured payload may take: half. The payload is sized
    /// first and the text gets whatever the payload left, so the two together stay within the budget.
    /// </summary>
    public static int StructuredShare(int maxResultBytes)
    {
        return maxResultBytes / 2;
    }

    /// <summary>The UTF-8 size of <paramref name="payload"/> as it is serialized into the result.</summary>
    public static int ByteCount(JsonNode payload)
    {
        return SerializedByteCount(payload);
    }

    /// <summary>
    /// Renders one tool result's rows both ways within <paramref name="maxResultBytes"/> (the resolved
    /// <c>MaxResultBytes</c>), measured as UTF-8: the structured payload is built first, within
    /// <see cref="StructuredShare"/>, and the text (<paramref name="textPrefix"/>, the markdown table and its notes) gets
    /// the rest. Either rendering that had to leave rows out says so (the budget notice in the text, truncated and
    /// rows_omitted_for_size in the payload).
    /// </summary>
    public static (string Text, JsonNode Structured) FormatResultWithinBudget(
        string textPrefix,
        IReadOnlyList<Dictionary<string, object?>> rows,
        int maxRows,
        int maxResultBytes)
    {
        var structured = BuildStructuredPayloadInternal(rows, maxRows, StructuredShare(maxResultBytes));
        var textBudget = maxResultBytes - SerializedByteCount(structured) - Encoding.UTF8.GetByteCount(textPrefix);

        return (textPrefix + FormatResultsAsMarkdownInternal(rows, maxRows, textBudget), structured);
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

    /// <summary>
    /// Cuts <paramref name="text"/> to at most <paramref name="maxBytes"/> bytes (notice included) on a character
    /// boundary, appending a note that it was cut; text within the budget is returned as is. Bytes are counted as the
    /// JSON string the SDK sends, by an upper bound (every character that may be escaped counts as its longest escape),
    /// so the wire text never exceeds the budget. A budget too small for the notice returns a cut without it.
    /// </summary>
    public static string WithinByteBudget(string text, int maxBytes)
    {
        if (EscapedSize(text) <= maxBytes)
        {
            return text;
        }

        const string notice = "\n\n_Truncated: the response reached the result size limit._\n";
        var noticeBytes = EscapedSize(notice);
        var suffix = noticeBytes <= maxBytes ? notice : string.Empty;
        var budget = Math.Max(0, maxBytes - (suffix.Length == 0 ? 0 : noticeBytes));
        var length = 0;
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var runeBytes = EscapedSize(rune);
            if (bytes + runeBytes > budget)
            {
                break;
            }

            bytes += runeBytes;
            length += rune.Utf16SequenceLength;
        }

        return text[..length] + suffix;
    }

    private static int EscapedSize(string text)
    {
        var total = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            total += EscapedSize(rune);
        }

        return total;
    }

    // Upper bound of a rune's size in a JSON string: printable ASCII is itself, except the characters an encoder may
    // write as \u00XX (6); control characters take 6; anything else may be written as \uXXXX per UTF-16 unit (6 or 12).
    private static int EscapedSize(Rune rune)
    {
        if (rune.Value < 0x80)
        {
            return rune.Value < 0x20 || rune.Value == 0x7F || "\"\\<>&'+`".Contains((char)rune.Value) ? 6 : 1;
        }

        return rune.Utf16SequenceLength * 6;
    }

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

    private static string? ResolveReadableProjectId(
        IProjectContext context,
        int? requestedProjectId,
        out int projectId)
    {
        projectId = 0;

        if (context.AccessUndetermined)
        {
            return ProjectContextFactory.AccessUndeterminedMessage;
        }

        if (requestedProjectId.HasValue)
        {
            if (context.AllowedProjectIds?.Contains(requestedProjectId.Value) != true)
            {
                return $"Access denied: you do not have access to project {requestedProjectId.Value}.";
            }

            projectId = requestedProjectId.Value;
            context.ActiveProjectId = projectId;
            return null;
        }

        // No project requested — try auto-resolve
        if (context.AllowedProjectIds == null || context.AllowedProjectIds.Count == 0)
        {
            return "No projects are available to you. Ask your Beacon administrator for access to a project.";
        }

        if (context.AllowedProjectIds.Count == 1)
        {
            projectId = context.AllowedProjectIds[0];
            context.ActiveProjectId = projectId;
            return null;
        }

        return $"Multiple projects available (IDs: {string.Join(", ", context.AllowedProjectIds)}). Specify project_id parameter to select one.";
    }

    private static string FormatResultsAsMarkdownInternal<T>(IReadOnlyList<T> rows, int maxRows, int maxResultBytes) where T : IDictionary<string, object?>
    {
        if (rows.Count == 0) return "No results returned.\n";

        var columns = rows[0].Keys.ToList();
        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", columns)).Append(" |\n");
        sb.Append("| ").Append(string.Join(" | ", columns.Select(_ => "---"))).Append(" |\n");

        // Result-budget honesty: never silently drop rows. rows.Count == maxRows most likely means
        // the SQL-level row cap was hit, so the full result set may be larger than what came back.
        var rowCapNotice = rows.Count > maxRows
            ? $"\n_Showing {maxRows} of {rows.Count} rows (truncated). Narrow the query or raise max_rows._\n"
            : rows.Count == maxRows
                ? $"\n_Row cap of {maxRows} reached — the result set may be truncated._\n"
                : "";

        // The markdown table honors the size budget measured in UTF-8 BYTES (R6-4: counting UTF-16
        // chars against a byte limit under-counts multibyte content — emoji/CJK cells could blow past
        // the byte budget on the wire): a row set with wide cell values can blow the response past what
        // MCP clients accept even when the row COUNT is within maxRows. Stop emitting rows once the
        // budget is reached and say so explicitly; the notes appended after the rows are counted in.
        var emittedBytes = Encoding.UTF8.GetByteCount(sb.ToString())
            + Encoding.UTF8.GetByteCount(SizeBudgetNotice)
            + Encoding.UTF8.GetByteCount(rowCapNotice);
        var budgetReached = false;
        foreach (var row in rows.Take(maxRows))
        {
            var line = "| " + string.Join(" | ", columns.Select(c =>
                row.TryGetValue(c, out var v) ? (v?.ToString() ?? "NULL") : "NULL")) + " |\n";

            var lineBytes = Encoding.UTF8.GetByteCount(line);
            if (emittedBytes + lineBytes > maxResultBytes)
            {
                budgetReached = true;
                break;
            }

            emittedBytes += lineBytes;
            sb.Append(line);
        }

        if (budgetReached)
        {
            sb.Append(SizeBudgetNotice);
        }

        sb.Append(rowCapNotice);

        return sb.ToString();
    }

    private static JsonNode BuildStructuredPayloadInternal<T>(IReadOnlyList<T> rows, int maxRows, int maxResultBytes) where T : IDictionary<string, object?>
    {
        var columns = rows.Count > 0
            ? rows[0].Keys.ToList()
            : new List<string>();

        var rowBudget = Math.Min(rows.Count, maxRows);
        var payload = BuildPayloadNode(rows, columns, rowBudget, maxRows);

        // Enforce the byte budget: halve the row count until the serialized payload fits (or no
        // rows are left), then say so explicitly — never silently return an oversized payload.
        var trimmedForSize = false;
        while (rowBudget > 0 && SerializedByteCount(payload) > maxResultBytes)
        {
            rowBudget /= 2;
            trimmedForSize = true;
            payload = BuildPayloadNode(rows, columns, rowBudget, maxRows);
        }

        if (trimmedForSize)
        {
            payload["truncated"] = true;
            payload["rows_omitted_for_size"] = true;
        }

        return payload;
    }

    private static JsonObject BuildPayloadNode<T>(IReadOnlyList<T> rows, List<string> columns, int rowBudget, int maxRows) where T : IDictionary<string, object?>
    {
        var rowsNode = new JsonArray();
        foreach (var row in rows.Take(rowBudget))
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

    private static int SerializedByteCount(JsonNode payload)
    {
        return Encoding.UTF8.GetByteCount(payload.ToJsonString());
    }

    private static JsonNode? ToJsonValue(object? value)
    {
        // Temporal and Guid values get explicit culture-invariant branches — the ToString() fallback
        // is culture-sensitive, so a comma-decimal or non-Gregorian server culture would leak localized
        // date text into the machine-readable payload. "O" is the ISO-8601 round-trip format.
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
            DateTime x => JsonValue.Create(x.ToString("O", CultureInfo.InvariantCulture)),
            DateTimeOffset x => JsonValue.Create(x.ToString("O", CultureInfo.InvariantCulture)),
            DateOnly x => JsonValue.Create(x.ToString("O", CultureInfo.InvariantCulture)),
            TimeOnly x => JsonValue.Create(x.ToString("O", CultureInfo.InvariantCulture)),
            Guid x => JsonValue.Create(x.ToString()),
            _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture))
        };
    }
}

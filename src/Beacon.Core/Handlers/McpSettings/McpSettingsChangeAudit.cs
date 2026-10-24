using System.Globalization;
using Microsoft.Extensions.Logging;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities;
using McpGlobalSettings = Beacon.Core.Data.Entities.McpSettings;

namespace Beacon.Core.Handlers.McpSettings;

/// <summary>
/// Records every change to the MCP settings, global and per project, in the settings history the admin settings already
/// keep (<see cref="AppSettingHistory"/>, read by an Auditor or a SecurityAdmin): one row per changed setting, keyed
/// <c>Mcp.{Setting}</c> or <c>Mcp.Project.{projectId}.{Setting}</c>, with the value before and after, when, and the
/// caller's id (<c>BeaconActor.LogId</c>). Only the settings named in <see cref="McpSettingFields"/> are recorded (the
/// overridable ones for a project row) — a property added to a settings row later stays out of the history until it is
/// listed there, so a secret is never written there by default. Changes are found on the full values; only the stored copy of a long text value (a prompt, a tool
/// description) is cut to the column's length, so an edit past that length is still recorded. A change is also noted in
/// the log by setting name and ids only (§1.11).
/// </summary>
internal static class McpSettingsChangeAudit
{
    public const string GlobalKeyPrefix = "Mcp.";

    /// <summary>The longest value a history row stores.</summary>
    public const int MaxValueLength = 2000;

    private const string Truncated = "…";

    public static string ProjectKeyPrefix(int projectId) => $"{GlobalKeyPrefix}Project.{projectId.ToString(CultureInfo.InvariantCulture)}.";

    /// <summary>The listed global settings of <paramref name="row"/>, formatted in full.</summary>
    public static Dictionary<string, string?> Snapshot(McpGlobalSettings row)
    {
        return McpSettingFields.All.ToDictionary(x => x.Name, x => Format(x.GetStored(row)), StringComparer.Ordinal);
    }

    /// <summary>The listed project overrides of <paramref name="row"/>, formatted in full.</summary>
    public static Dictionary<string, string?> Snapshot(McpProjectSettings row)
    {
        return McpSettingFields.Overridables.ToDictionary(x => x.Name, x => Format(x.GetStored(row)), StringComparer.Ordinal);
    }

    /// <summary>
    /// One history row per setting whose full value differs between the two snapshots; the row stores each value cut to
    /// <see cref="MaxValueLength"/>.
    /// </summary>
    public static List<AppSettingHistory> Changes(
        string keyPrefix,
        IReadOnlyDictionary<string, string?> before,
        IReadOnlyDictionary<string, string?> after,
        string? actorId,
        DateTime changedAt)
    {
        return after
            .Where(x => !string.Equals(before.GetValueOrDefault(x.Key), x.Value, StringComparison.Ordinal))
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x =>
                new AppSettingHistory
                {
                    SettingKey = keyPrefix + x.Key,
                    OldValue = ForStorage(before.GetValueOrDefault(x.Key)),
                    NewValue = ForStorage(x.Value),
                    ChangedAt = changedAt,
                    ChangedByUserId = actorId
                })
            .ToList();
    }

    /// <summary>Notes a change in the log: who, which settings, which project — never a value.</summary>
    public static void Log(ILogger logger, IReadOnlyList<AppSettingHistory> changes, string? actorId, int? projectId)
    {
        if (changes.Count == 0)
        {
            return;
        }

        logger.LogInformation(
            "MCP settings changed by {ActorId} for {Scope}: {Settings}",
            actorId ?? "unknown",
            projectId == null ? "the global settings" : $"project {projectId.Value.ToString(CultureInfo.InvariantCulture)}",
            changes
                .Select(x => x.SettingKey)
                .ToList());
    }

    private static string? Format(object? value)
    {
        return value switch
        {
            null => null,
            bool x => x ? "true" : "false",
            IFormattable x => x.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }

    private static string? ForStorage(string? text)
    {
        return text is { Length: > MaxValueLength }
            ? text[..(MaxValueLength - Truncated.Length)] + Truncated
            : text;
    }
}

using System.Reflection;
using System.Text.Json;
using Beacon.Core.Data.Entities;
using Beacon.Core.Models;

namespace Beacon.Core.Configuration;

/// <summary>
/// One MCP setting: its name, whether a project may override it, and its property on each of the four settings types —
/// the global row (<see cref="McpSettings"/>), the global settings (<see cref="McpSettingsData"/>), a project's row
/// (<see cref="McpProjectSettings"/>) and a project's overrides (<see cref="McpProjectSettingsData"/>, null = inherit).
/// </summary>
internal sealed class McpSettingField
{
    private readonly PropertyInfo _settings;
    private readonly PropertyInfo _row;
    private readonly PropertyInfo? _overrides;
    private readonly PropertyInfo? _projectRow;

    internal McpSettingField(string name, bool overridable, bool requiresPositive)
    {
        Name = name;
        RequiresPositive = requiresPositive;
        IsPatternList = name == nameof(McpSettingsData.CustomPiiPatterns);
        _settings = Property(typeof(McpSettingsData), name);
        _row = Property(typeof(McpSettings), name);
        if (overridable)
        {
            _overrides = Property(typeof(McpProjectSettingsData), name);
            _projectRow = Property(typeof(McpProjectSettings), name);
        }
    }

    public string Name { get; }

    /// <summary>True when a project may override the setting; the prompts and tool descriptions stay global.</summary>
    public bool Overridable => _overrides != null;

    /// <summary>
    /// A value below one no reader can use (a timeout of 0 cancels every statement, a concurrency of 0 refuses every
    /// call): a project override below one is no override (<see cref="McpSettingFields.Overlay"/>).
    /// </summary>
    public bool RequiresPositive { get; }

    /// <summary>The custom PII patterns: a list in the settings, its JSON in the rows.</summary>
    public bool IsPatternList { get; }

    public object? Get(McpSettingsData settings) => _settings.GetValue(settings);

    public void Set(McpSettingsData settings, object? value) => _settings.SetValue(settings, value);

    public object? GetOverride(McpProjectSettingsData overrides) => OverridesProperty.GetValue(overrides);

    public object? GetStored(McpSettings row) => _row.GetValue(row);

    public object? GetStored(McpProjectSettings row) => ProjectRowProperty.GetValue(row);

    internal void SetStored(McpSettings row, object? value) => _row.SetValue(row, value);

    internal void SetStored(McpProjectSettings row, object? value) => ProjectRowProperty.SetValue(row, value);

    internal void SetOverride(McpProjectSettingsData overrides, object? value) => OverridesProperty.SetValue(overrides, value);

    private PropertyInfo OverridesProperty => _overrides ?? throw NotOverridable();

    private PropertyInfo ProjectRowProperty => _projectRow ?? throw NotOverridable();

    private InvalidOperationException NotOverridable() => new($"The MCP setting {Name} has no per-project override.");

    private static PropertyInfo Property(Type type, string name)
    {
        return type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is { CanRead: true, CanWrite: true } property
            ? property
            : throw new InvalidOperationException($"{type.Name} has no settable property {name}.");
    }
}

/// <summary>
/// The MCP settings, one entry each: the single list the settings provider maps the rows with and overlays a project's
/// overrides by, the update handlers write the rows by, the settings history records (<c>McpSettingsChangeAudit</c>),
/// the ranges and the deployment locks name their field in. A new setting is a property on the settings types and one
/// entry here; <c>McpSettingFieldParityTests</c> fails until both exist. Only the settings listed here are written or
/// recorded, so a property added to a row stays out of the settings history until it is listed.
/// </summary>
internal static class McpSettingFields
{
    public static IReadOnlyList<McpSettingField> All { get; } =
    [
        GlobalOnly(nameof(McpSettingsData.AskSystemPrompt)),
        GlobalOnly(nameof(McpSettingsData.GlobalInstruction)),
        GlobalOnly(nameof(McpSettingsData.GetContextDescription)),
        GlobalOnly(nameof(McpSettingsData.QueryDescription)),
        GlobalOnly(nameof(McpSettingsData.GetDocumentationDescription)),
        GlobalOnly(nameof(McpSettingsData.AskDescription)),
        GlobalOnly(nameof(McpSettingsData.SearchDescription)),
        Overridable(nameof(McpSettingsData.MaxRowLimit)),
        Overridable(nameof(McpSettingsData.EnforceReadOnly)),
        Overridable(nameof(McpSettingsData.EnablePiiDetection)),
        Overridable(nameof(McpSettingsData.CustomPiiPatterns)),
        Overridable(nameof(McpSettingsData.EnableSampleValueCollection)),
        Overridable(nameof(McpSettingsData.EnableLearning)),
        Overridable(nameof(McpSettingsData.LearningAutoApproveThreshold)),
        Overridable(nameof(McpSettingsData.LearningInjectionBudgetChars)),
        Overridable(nameof(McpSettingsData.LearningSignalRetentionDays)),
        Overridable(nameof(McpSettingsData.EnableSelfConsistency)),
        Overridable(nameof(McpSettingsData.SelfConsistencyCandidateCount)),
        Overridable(nameof(McpSettingsData.EnableEvalJudge)),
        Overridable(nameof(McpSettingsData.EnableSemanticRetrieval)),
        Overridable(nameof(McpSettingsData.ExemplarTopK)),
        Overridable(nameof(McpSettingsData.EnableReplayVerification)),
        Overridable(nameof(McpSettingsData.LearningReplayMinFlips)),
        Overridable(nameof(McpSettingsData.EnableContextualRetrieval)),
        Overridable(nameof(McpSettingsData.DocChunkWindowSentences)),
        Overridable(nameof(McpSettingsData.DocChunkOverlapSentences)),
        Overridable(nameof(McpSettingsData.GlossaryTopK)),
        Overridable(nameof(McpSettingsData.DocChunkTopK)),
        Overridable(nameof(McpSettingsData.EnableGoldenExemplars)),
        Overridable(nameof(McpSettingsData.GoldenExemplarTopK)),
        Overridable(nameof(McpSettingsData.GoldenExemplarBudgetChars)),
        Overridable(nameof(McpSettingsData.EnableValueGrounding)),
        Overridable(nameof(McpSettingsData.ValueGroundingMaxProbes)),
        Overridable(nameof(McpSettingsData.EnableSemanticLint)),
        Overridable(nameof(McpSettingsData.SelfConsistencyMinTables)),
        Overridable(nameof(McpSettingsData.RetainQueryContent)),
        Overridable(nameof(McpSettingsData.StatementTimeoutSeconds), requiresPositive: true),
        Overridable(nameof(McpSettingsData.MaxResultBytes), requiresPositive: true),
        Overridable(nameof(McpSettingsData.MaxExplainCost)),
        Overridable(nameof(McpSettingsData.MaxConcurrentQueriesPerKey), requiresPositive: true),
        Overridable(nameof(McpSettingsData.AllowExplicitFeedbackContent))
    ];

    /// <summary>The settings a project may override, in table order.</summary>
    public static IReadOnlyList<McpSettingField> Overridables { get; } = All
        .Where(x => x.Overridable)
        .ToList();

    private static readonly Dictionary<string, McpSettingField> ByName = All.ToDictionary(x => x.Name, StringComparer.Ordinal);

    public static McpSettingField Named(string name)
    {
        return ByName.TryGetValue(name, out var field)
            ? field
            : throw new InvalidOperationException($"{name} is not an MCP setting.");
    }

    /// <summary>
    /// The global row as settings. <paramref name="readPatterns"/> parses the stored pattern JSON (null when there is none
    /// or it cannot be read: no custom patterns).
    /// </summary>
    public static McpSettingsData ToSettings(McpSettings row, Func<string?, List<string>?> readPatterns)
    {
        var settings = new McpSettingsData();
        foreach (var field in All)
        {
            var stored = field.GetStored(row);
            field.Set(settings, field.IsPatternList ? readPatterns((string?)stored) ?? [] : stored);
        }

        return settings;
    }

    /// <summary>A project's row as overrides; a pattern list that cannot be read inherits the global list.</summary>
    public static McpProjectSettingsData ToOverrides(McpProjectSettings row, Func<string?, List<string>?> readPatterns)
    {
        var overrides = new McpProjectSettingsData();
        foreach (var field in Overridables)
        {
            var stored = field.GetStored(row);
            field.SetOverride(overrides, field.IsPatternList ? readPatterns((string?)stored) : stored);
        }

        return overrides;
    }

    /// <summary>Writes every setting into the global row as sent, except the ones named in <paramref name="keep"/>.</summary>
    public static void Store(McpSettingsData settings, McpSettings row, IReadOnlySet<string> keep)
    {
        foreach (var field in All.Where(x => !keep.Contains(x.Name)))
        {
            var value = field.Get(settings);

            // No custom patterns are stored as no JSON.
            field.SetStored(row, field.IsPatternList ? value is List<string> { Count: > 0 } patterns ? JsonSerializer.Serialize(patterns) : null : value);
        }
    }

    /// <summary>Writes every override into the project's row as sent (null = inherit), except the ones named in <paramref name="keep"/>.</summary>
    public static void Store(McpProjectSettingsData overrides, McpProjectSettings row, IReadOnlySet<string> keep)
    {
        foreach (var field in Overridables.Where(x => !keep.Contains(x.Name)))
        {
            var value = field.GetOverride(overrides);

            // A non-null EMPTY list is a real override ("no custom patterns for this project") and is stored as "[]".
            field.SetStored(row, field.IsPatternList ? value is List<string> patterns ? JsonSerializer.Serialize(patterns) : null : value);
        }
    }

    /// <summary>
    /// Lays a project's non-null overrides over <paramref name="effective"/>. An override no reader can use (below one,
    /// written before the write handlers checked the ranges) is no override: the field keeps its value and is reported in
    /// <paramref name="clamped"/>.
    /// </summary>
    public static void Overlay(McpSettingsData effective, McpProjectSettingsData overrides, ISet<string> clamped)
    {
        foreach (var field in Overridables)
        {
            var value = field.GetOverride(overrides);
            if (value == null)
            {
                continue;
            }

            if (field.RequiresPositive && (int)value < 1)
            {
                clamped.Add(field.Name);

                continue;
            }

            field.Set(effective, value is List<string> patterns ? new List<string>(patterns) : value);
        }
    }

    private static McpSettingField GlobalOnly(string name) => new(name, overridable: false, requiresPositive: false);

    private static McpSettingField Overridable(string name, bool requiresPositive = false) => new(name, overridable: true, requiresPositive);
}

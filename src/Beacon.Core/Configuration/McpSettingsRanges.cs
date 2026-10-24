using System.Globalization;
using Beacon.Core.Models;

namespace Beacon.Core.Configuration;

/// <summary>
/// The range every numeric MCP setting is saved in, on the global settings and a project's overrides alike (a project's
/// empty override is not checked: it inherits). A value below its minimum would turn its limit off or break what reads it
/// (a timeout of 0 cancels every statement at once, a concurrency of 0 refuses every call); 0 is allowed only where it
/// means "none" to its reader (a character budget, the value-grounding probes, overlapping sentences). A maximum is set
/// only where a larger value overflows where it is used. A deployment ceiling is no bound here: a value above it is
/// clamped when the settings are read, and the ceiling can change.
/// </summary>
internal static class McpSettingsRanges
{
    /// <summary>The largest row limit saved: one more row is read past it, which must still be an <c>int</c>.</summary>
    public const int MaxRowLimitMaximum = QueryExecutionOptions.MaxAllowedExecutionRows;

    /// <summary>The longest statement timeout saved, and the longest a stored one is read as: one hour.</summary>
    public const int StatementTimeoutSecondsMaximum = QueryExecutionOptions.MaxAllowedStatementTimeoutSeconds;

    /// <summary>The longest retention window saved, in days (a cutoff date is computed from it): 100 years.</summary>
    public const int LearningSignalRetentionDaysMaximum = 36_500;

    /// <summary>The largest plan-cost limit the stored column (precision 18, scale 2) holds.</summary>
    public const decimal MaxExplainCostMaximum = 9_999_999_999_999_999.99m;

    private static readonly FieldRange[] Ranges =
    [
        new(nameof(McpSettingsData.MaxRowLimit), 1, MaxRowLimitMaximum),
        new(nameof(McpSettingsData.LearningAutoApproveThreshold), 0, 1),
        new(nameof(McpSettingsData.LearningInjectionBudgetChars), 0, null),
        new(nameof(McpSettingsData.LearningSignalRetentionDays), 1, LearningSignalRetentionDaysMaximum),
        new(nameof(McpSettingsData.SelfConsistencyCandidateCount), 1, null),
        new(nameof(McpSettingsData.ExemplarTopK), 1, null),
        new(nameof(McpSettingsData.LearningReplayMinFlips), 1, null),
        new(nameof(McpSettingsData.DocChunkWindowSentences), 1, null),
        new(nameof(McpSettingsData.DocChunkOverlapSentences), 0, null),
        new(nameof(McpSettingsData.GlossaryTopK), 1, null),
        new(nameof(McpSettingsData.DocChunkTopK), 1, null),
        new(nameof(McpSettingsData.GoldenExemplarTopK), 1, null),
        new(nameof(McpSettingsData.GoldenExemplarBudgetChars), 0, null),
        new(nameof(McpSettingsData.ValueGroundingMaxProbes), 0, null),
        new(nameof(McpSettingsData.SelfConsistencyMinTables), 1, null),
        new(nameof(McpSettingsData.StatementTimeoutSeconds), 1, StatementTimeoutSecondsMaximum),
        new(nameof(McpSettingsData.MaxResultBytes), 1, null),
        new(nameof(McpSettingsData.MaxExplainCost), 0, MaxExplainCostMaximum),
        new(nameof(McpSettingsData.MaxConcurrentQueriesPerKey), 1, null)
    ];

    /// <summary>The fields that have a range, by name (the same on the global settings and a project's overrides).</summary>
    internal static IEnumerable<string> Fields => Ranges.Select(x => x.Field);

    /// <summary>Refuses the first numeric field of the global settings outside its range (<see cref="InvalidOperationException"/>).</summary>
    public static void Check(McpSettingsData data)
    {
        foreach (var range in Ranges)
        {
            range.Check(Number(McpSettingFields.Named(range.Field).Get(data)));
        }
    }

    /// <summary>Refuses the first numeric override of a project outside its range; an empty override is not checked.</summary>
    public static void Check(McpProjectSettingsData data)
    {
        foreach (var range in Ranges)
        {
            range.Check(Number(McpSettingFields.Named(range.Field).GetOverride(data)));
        }
    }

    // A threshold that is not a finite number is outside every range; decimal holds every finite value in it exactly.
    private static decimal? Number(object? value)
    {
        return value switch
        {
            null => null,
            int x => x,
            decimal x => x,
            double x => double.IsFinite(x) && Math.Abs(x) < 1e15 ? (decimal)x : decimal.MinValue,
            _ => throw new InvalidOperationException($"An MCP setting of type {value.GetType().Name} has no range.")
        };
    }

    private sealed record FieldRange(
        string Field,
        decimal Minimum,
        decimal? Maximum)
    {
        public void Check(decimal? value)
        {
            if (value is not { } number)
            {
                return;
            }

            if (number >= Minimum && (Maximum == null || number <= Maximum))
            {
                return;
            }

            var allowed = Maximum is { } maximum
                ? $"between {Format(Minimum)} and {Format(maximum)}"
                : $"at least {Format(Minimum)}";

            throw new InvalidOperationException($"{Field} must be {allowed}.");
        }

        private static string Format(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    }
}

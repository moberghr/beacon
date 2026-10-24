using Microsoft.Extensions.Options;
using Beacon.Core.Services.Retention;

namespace Beacon.Core.Configuration;

/// <summary>
/// How long Beacon keeps its run history and logs, and their content (<c>Beacon:Retention</c>). The data retention job
/// (<see cref="IDataRetentionService"/>, which the host schedules) deletes the rows of every retention target older than
/// its window, and clears the content (SQL text, result rows, error text, LLM output) of the content stores' rows older
/// than <see cref="ContentDays"/>. The windows are read once at startup; nothing changes them at runtime.
/// </summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Beacon:Retention";

    /// <summary>The row window recommended for every target: 90 days. Not a default: nothing is deleted unless configured.</summary>
    public const int RecommendedDays = 90;

    /// <summary>The longest window accepted: one hundred years.</summary>
    public const int MaxDays = 36500;

    /// <summary>The content window recommended for regulated deployments: 30 days.</summary>
    public const int RecommendedRegulatedContentDays = 30;

    /// <summary>
    /// The longest one run of the data retention job works: 50 minutes, under the sample host's one-hour job timeout. A
    /// host that schedules the job itself gives it a longer timeout; what a run leaves, the next run continues.
    /// </summary>
    public const int RunBudgetMinutes = 50;

    /// <summary>
    /// Days the rows of every retention target are kept; <c>0</c> (the default) keeps them forever, so an upgrade deletes
    /// nothing. <see cref="RecommendedDays"/> is the recommended window.
    /// </summary>
    public int Days { get; set; }

    /// <summary>
    /// Per-target windows by target name (<c>Beacon:Retention:Targets:subscription-runs</c>, ...), replacing
    /// <see cref="Days"/> for that target; <c>0</c> keeps its rows forever. An unknown name fails startup.
    /// </summary>
    public Dictionary<string, int> Targets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Days the content of the content stores' rows is kept; the row itself stays until its target's window. <c>0</c>
    /// (the default) clears none by age. 30 days (<see cref="RecommendedRegulatedContentDays"/>) is recommended for
    /// regulated deployments. While the content lock is on (<c>RetainQueryContent</c> off globally, or
    /// <c>Beacon:Mcp:ForceNoContentRetention</c>), stored result snapshots are cleared whatever their age.
    /// </summary>
    public int ContentDays { get; set; }

    /// <summary>
    /// The deployment lock: no retention window may keep data forever (<see cref="Days"/>, also while every target sets its
    /// own window, every target, <see cref="ContentDays"/> and <c>Beacon:Mcp:Audit:RetentionDays</c> must be set and greater
    /// than zero, or the host fails to start), and the one window Admins edit at runtime, the learning-signal retention,
    /// never resolves to more than <see cref="Days"/>. The stores in <see cref="RetentionLock.ExemptStores"/> are not run
    /// history and are outside it by design.
    /// </summary>
    public bool Locked { get; set; }

    /// <summary>The window of one target: its own when configured, else <see cref="Days"/>.</summary>
    public int DaysFor(string target)
    {
        return Targets.TryGetValue(target, out var days) ? days : Days;
    }
}

internal sealed class RetentionOptionsValidator(
    IEnumerable<IRetentionTarget> targets,
    IOptions<McpDeploymentOptions> mcpOptions) : IValidateOptions<RetentionOptions>
{
    public ValidateOptionsResult Validate(string? name, RetentionOptions options)
    {
        var failures = new List<string>();
        RequireRange(failures, nameof(RetentionOptions.Days), options.Days);
        RequireRange(failures, nameof(RetentionOptions.ContentDays), options.ContentDays);

        var known = new HashSet<string>(targets.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var target in options.Targets)
        {
            if (!known.Contains(target.Key))
            {
                failures.Add($"{RetentionOptions.SectionName}:{nameof(RetentionOptions.Targets)}:{target.Key} names no retention target; known targets: {string.Join(", ", known.Order(StringComparer.Ordinal))}.");
            }

            RequireRange(failures, $"{nameof(RetentionOptions.Targets)}:{target.Key}", target.Value);
        }

        if (options.Locked)
        {
            failures.AddRange(KeptForever(options, known, mcpOptions.Value)
                .Distinct(StringComparer.Ordinal)
                .Select(x => $"{RetentionOptions.SectionName}:{nameof(RetentionOptions.Locked)} is on, so {x} must be greater than zero: no run history or log may be kept forever (outside the lock by design: {RetentionLock.ExemptStores})."));
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static IEnumerable<string> KeptForever(RetentionOptions options, IEnumerable<string> targets, McpDeploymentOptions mcpOptions)
    {
        // Days also caps the learning-signal retention, so it is required even when every target sets its own window.
        if (options.Days == 0)
        {
            yield return $"{RetentionOptions.SectionName}:{nameof(RetentionOptions.Days)}";
        }

        foreach (var target in targets.Order(StringComparer.Ordinal))
        {
            if (options.DaysFor(target) == 0)
            {
                var setting = options.Targets.ContainsKey(target) ? $"{nameof(RetentionOptions.Targets)}:{target}" : nameof(RetentionOptions.Days);

                yield return $"{RetentionOptions.SectionName}:{setting}";
            }
        }

        if (options.ContentDays == 0)
        {
            yield return $"{RetentionOptions.SectionName}:{nameof(RetentionOptions.ContentDays)}";
        }

        if (mcpOptions.Audit?.RetentionDays is null or 0)
        {
            yield return $"{McpDeploymentOptions.SectionName}:Audit:{nameof(McpAuditOptions.RetentionDays)}";
        }
    }

    private static void RequireRange(List<string> failures, string field, int value)
    {
        if (value is < 0 or > RetentionOptions.MaxDays)
        {
            failures.Add($"{RetentionOptions.SectionName}:{field} must be between 0 (keep forever) and {RetentionOptions.MaxDays} days.");
        }
    }
}

/// <summary>What <see cref="RetentionOptions.Locked"/> does to the settings Admins edit at runtime.</summary>
internal static class RetentionLock
{
    /// <summary>
    /// What the lock does not purge, by design: records that are configuration or governance, not run history. The query
    /// step change history is the record of how a saved query's SQL changed and who or which AI actor run changed it; it
    /// goes with its query, and the executions and plans it names are kept for it.
    /// </summary>
    internal const string ExemptStores =
        "the security audit trail (its own Beacon:Audit:RetentionDays), learning signals (their own window, capped by Days), "
        + "the query step change history, settings history, and configuration: projects, data sources, metadata, queries, "
        + "subscriptions, tasks, glossary, golden eval cases and learned patterns";

    /// <summary>Under the lock the learning-signal retention ceiling is at most <see cref="RetentionOptions.Days"/>.</summary>
    internal static void CapLearningSignals(McpDeploymentOptions mcp, RetentionOptions retention)
    {
        if (!retention.Locked || retention.Days <= 0)
        {
            return;
        }

        mcp.Ceilings ??= new McpCeilingOptions();
        mcp.Ceilings.LearningSignalRetentionDays = Math.Min(mcp.Ceilings.LearningSignalRetentionDays ?? int.MaxValue, retention.Days);
    }
}

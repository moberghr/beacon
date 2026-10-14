using System.Globalization;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Entities;
using McpGlobalSettings = Beacon.Core.Data.Entities.McpSettings;

namespace Beacon.Core.Handlers.McpSettings;

/// <summary>
/// Records every change to the MCP settings, global and per project, in the settings history the admin settings already
/// keep (<see cref="AppSettingHistory"/>, read by an Auditor or a SecurityAdmin): one row per changed setting, keyed
/// <c>Mcp.{Setting}</c> or <c>Mcp.Project.{projectId}.{Setting}</c>, with the value before and after, when, and the
/// caller's id (<c>BeaconActor.LogId</c>). Only the settings named on an explicit list per row type are recorded — a
/// property added to a settings row later stays out of the history until it is listed, so a secret is never written there
/// by default. Changes are found on the full values; only the stored copy of a long text value (a prompt, a tool
/// description) is cut to the column's length, so an edit past that length is still recorded. A change is also noted in
/// the log by setting name and ids only (§1.11).
/// </summary>
internal static class McpSettingsChangeAudit
{
    public const string GlobalKeyPrefix = "Mcp.";

    /// <summary>The longest value a history row stores.</summary>
    public const int MaxValueLength = 2000;

    private const string Truncated = "…";

    // The global settings a history row records, named one by one.
    private static readonly (string Name, Func<McpGlobalSettings, object?> Value)[] GlobalSettings =
    [
        (nameof(McpGlobalSettings.AskSystemPrompt), x => x.AskSystemPrompt),
        (nameof(McpGlobalSettings.GlobalInstruction), x => x.GlobalInstruction),
        (nameof(McpGlobalSettings.GetContextDescription), x => x.GetContextDescription),
        (nameof(McpGlobalSettings.SearchDescription), x => x.SearchDescription),
        (nameof(McpGlobalSettings.QueryDescription), x => x.QueryDescription),
        (nameof(McpGlobalSettings.GetDocumentationDescription), x => x.GetDocumentationDescription),
        (nameof(McpGlobalSettings.AskDescription), x => x.AskDescription),
        (nameof(McpGlobalSettings.MaxRowLimit), x => x.MaxRowLimit),
        (nameof(McpGlobalSettings.EnforceReadOnly), x => x.EnforceReadOnly),
        (nameof(McpGlobalSettings.EnablePiiDetection), x => x.EnablePiiDetection),
        (nameof(McpGlobalSettings.CustomPiiPatterns), x => x.CustomPiiPatterns),
        (nameof(McpGlobalSettings.EnableSampleValueCollection), x => x.EnableSampleValueCollection),
        (nameof(McpGlobalSettings.EnableLearning), x => x.EnableLearning),
        (nameof(McpGlobalSettings.LearningAutoApproveThreshold), x => x.LearningAutoApproveThreshold),
        (nameof(McpGlobalSettings.LearningInjectionBudgetChars), x => x.LearningInjectionBudgetChars),
        (nameof(McpGlobalSettings.LearningSignalRetentionDays), x => x.LearningSignalRetentionDays),
        (nameof(McpGlobalSettings.EnableSelfConsistency), x => x.EnableSelfConsistency),
        (nameof(McpGlobalSettings.SelfConsistencyCandidateCount), x => x.SelfConsistencyCandidateCount),
        (nameof(McpGlobalSettings.EnableEvalJudge), x => x.EnableEvalJudge),
        (nameof(McpGlobalSettings.EnableSemanticRetrieval), x => x.EnableSemanticRetrieval),
        (nameof(McpGlobalSettings.ExemplarTopK), x => x.ExemplarTopK),
        (nameof(McpGlobalSettings.EnableReplayVerification), x => x.EnableReplayVerification),
        (nameof(McpGlobalSettings.LearningReplayMinFlips), x => x.LearningReplayMinFlips),
        (nameof(McpGlobalSettings.EnableContextualRetrieval), x => x.EnableContextualRetrieval),
        (nameof(McpGlobalSettings.DocChunkWindowSentences), x => x.DocChunkWindowSentences),
        (nameof(McpGlobalSettings.DocChunkOverlapSentences), x => x.DocChunkOverlapSentences),
        (nameof(McpGlobalSettings.GlossaryTopK), x => x.GlossaryTopK),
        (nameof(McpGlobalSettings.DocChunkTopK), x => x.DocChunkTopK),
        (nameof(McpGlobalSettings.EnableGoldenExemplars), x => x.EnableGoldenExemplars),
        (nameof(McpGlobalSettings.GoldenExemplarTopK), x => x.GoldenExemplarTopK),
        (nameof(McpGlobalSettings.GoldenExemplarBudgetChars), x => x.GoldenExemplarBudgetChars),
        (nameof(McpGlobalSettings.EnableValueGrounding), x => x.EnableValueGrounding),
        (nameof(McpGlobalSettings.ValueGroundingMaxProbes), x => x.ValueGroundingMaxProbes),
        (nameof(McpGlobalSettings.EnableSemanticLint), x => x.EnableSemanticLint),
        (nameof(McpGlobalSettings.SelfConsistencyMinTables), x => x.SelfConsistencyMinTables),
        (nameof(McpGlobalSettings.RetainQueryContent), x => x.RetainQueryContent),
        (nameof(McpGlobalSettings.StatementTimeoutSeconds), x => x.StatementTimeoutSeconds),
        (nameof(McpGlobalSettings.MaxResultBytes), x => x.MaxResultBytes),
        (nameof(McpGlobalSettings.MaxExplainCost), x => x.MaxExplainCost),
        (nameof(McpGlobalSettings.MaxConcurrentQueriesPerKey), x => x.MaxConcurrentQueriesPerKey),
        (nameof(McpGlobalSettings.AllowExplicitFeedbackContent), x => x.AllowExplicitFeedbackContent)
    ];

    // The per-project overrides a history row records, named one by one (not the project link).
    private static readonly (string Name, Func<McpProjectSettings, object?> Value)[] ProjectSettings =
    [
        (nameof(McpProjectSettings.MaxRowLimit), x => x.MaxRowLimit),
        (nameof(McpProjectSettings.EnforceReadOnly), x => x.EnforceReadOnly),
        (nameof(McpProjectSettings.EnablePiiDetection), x => x.EnablePiiDetection),
        (nameof(McpProjectSettings.CustomPiiPatterns), x => x.CustomPiiPatterns),
        (nameof(McpProjectSettings.EnableSampleValueCollection), x => x.EnableSampleValueCollection),
        (nameof(McpProjectSettings.EnableLearning), x => x.EnableLearning),
        (nameof(McpProjectSettings.LearningAutoApproveThreshold), x => x.LearningAutoApproveThreshold),
        (nameof(McpProjectSettings.LearningInjectionBudgetChars), x => x.LearningInjectionBudgetChars),
        (nameof(McpProjectSettings.LearningSignalRetentionDays), x => x.LearningSignalRetentionDays),
        (nameof(McpProjectSettings.EnableSelfConsistency), x => x.EnableSelfConsistency),
        (nameof(McpProjectSettings.SelfConsistencyCandidateCount), x => x.SelfConsistencyCandidateCount),
        (nameof(McpProjectSettings.EnableEvalJudge), x => x.EnableEvalJudge),
        (nameof(McpProjectSettings.EnableSemanticRetrieval), x => x.EnableSemanticRetrieval),
        (nameof(McpProjectSettings.ExemplarTopK), x => x.ExemplarTopK),
        (nameof(McpProjectSettings.EnableReplayVerification), x => x.EnableReplayVerification),
        (nameof(McpProjectSettings.LearningReplayMinFlips), x => x.LearningReplayMinFlips),
        (nameof(McpProjectSettings.EnableContextualRetrieval), x => x.EnableContextualRetrieval),
        (nameof(McpProjectSettings.DocChunkWindowSentences), x => x.DocChunkWindowSentences),
        (nameof(McpProjectSettings.DocChunkOverlapSentences), x => x.DocChunkOverlapSentences),
        (nameof(McpProjectSettings.GlossaryTopK), x => x.GlossaryTopK),
        (nameof(McpProjectSettings.DocChunkTopK), x => x.DocChunkTopK),
        (nameof(McpProjectSettings.EnableGoldenExemplars), x => x.EnableGoldenExemplars),
        (nameof(McpProjectSettings.GoldenExemplarTopK), x => x.GoldenExemplarTopK),
        (nameof(McpProjectSettings.GoldenExemplarBudgetChars), x => x.GoldenExemplarBudgetChars),
        (nameof(McpProjectSettings.EnableValueGrounding), x => x.EnableValueGrounding),
        (nameof(McpProjectSettings.ValueGroundingMaxProbes), x => x.ValueGroundingMaxProbes),
        (nameof(McpProjectSettings.EnableSemanticLint), x => x.EnableSemanticLint),
        (nameof(McpProjectSettings.SelfConsistencyMinTables), x => x.SelfConsistencyMinTables),
        (nameof(McpProjectSettings.RetainQueryContent), x => x.RetainQueryContent),
        (nameof(McpProjectSettings.StatementTimeoutSeconds), x => x.StatementTimeoutSeconds),
        (nameof(McpProjectSettings.MaxResultBytes), x => x.MaxResultBytes),
        (nameof(McpProjectSettings.MaxExplainCost), x => x.MaxExplainCost),
        (nameof(McpProjectSettings.MaxConcurrentQueriesPerKey), x => x.MaxConcurrentQueriesPerKey),
        (nameof(McpProjectSettings.AllowExplicitFeedbackContent), x => x.AllowExplicitFeedbackContent)
    ];

    public static string ProjectKeyPrefix(int projectId) => $"{GlobalKeyPrefix}Project.{projectId.ToString(CultureInfo.InvariantCulture)}.";

    /// <summary>The listed global settings of <paramref name="row"/>, formatted in full.</summary>
    public static Dictionary<string, string?> Snapshot(McpGlobalSettings row)
    {
        return Snapshot(row, GlobalSettings);
    }

    /// <summary>The listed project overrides of <paramref name="row"/>, formatted in full.</summary>
    public static Dictionary<string, string?> Snapshot(McpProjectSettings row)
    {
        return Snapshot(row, ProjectSettings);
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

    private static Dictionary<string, string?> Snapshot<T>(T row, (string Name, Func<T, object?> Value)[] settings)
    {
        return settings.ToDictionary(x => x.Name, x => Format(x.Value(row)), StringComparer.Ordinal);
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

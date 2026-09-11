namespace Beacon.Core.Models;

/// <summary>
/// Per-project overrides as exchanged with the API and UI. Every member is nullable; <c>null</c> means "inherit
/// the global value". Mirrors <c>McpProjectSettings</c> minus the entity plumbing; <see cref="CustomPiiPatterns"/>
/// is the parsed list rather than the stored JSON.
/// </summary>
public class McpProjectSettingsData
{
    public int? MaxRowLimit { get; set; }
    public bool? EnforceReadOnly { get; set; }
    public bool? EnablePiiDetection { get; set; }
    public List<string>? CustomPiiPatterns { get; set; }
    public bool? EnableSampleValueCollection { get; set; }

    public bool? EnableLearning { get; set; }
    public double? LearningAutoApproveThreshold { get; set; }
    public int? LearningInjectionBudgetChars { get; set; }
    public int? LearningSignalRetentionDays { get; set; }

    public bool? EnableSelfConsistency { get; set; }
    public int? SelfConsistencyCandidateCount { get; set; }
    public bool? EnableEvalJudge { get; set; }
    public bool? EnableSemanticRetrieval { get; set; }
    public int? ExemplarTopK { get; set; }

    public bool? EnableReplayVerification { get; set; }
    public int? LearningReplayMinFlips { get; set; }

    public bool? EnableContextualRetrieval { get; set; }
    public int? DocChunkWindowSentences { get; set; }
    public int? DocChunkOverlapSentences { get; set; }
    public int? GlossaryTopK { get; set; }
    public int? DocChunkTopK { get; set; }

    public bool? EnableGoldenExemplars { get; set; }
    public int? GoldenExemplarTopK { get; set; }
    public int? GoldenExemplarBudgetChars { get; set; }

    public bool? EnableValueGrounding { get; set; }
    public int? ValueGroundingMaxProbes { get; set; }
    public bool? EnableSemanticLint { get; set; }
    public int? SelfConsistencyMinTables { get; set; }

    public bool? RetainQueryContent { get; set; }
    public int? StatementTimeoutSeconds { get; set; }
    public int? MaxResultBytes { get; set; }
    public decimal? MaxExplainCost { get; set; }
    public int? MaxConcurrentQueriesPerKey { get; set; }
    public bool? AllowExplicitFeedbackContent { get; set; }
}

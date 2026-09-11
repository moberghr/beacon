using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Entities.Projects;

namespace Beacon.Core.Data.Entities;

/// <summary>
/// Per-project overrides of <see cref="McpSettings"/>. Every field is nullable: <c>null</c> means "inherit the
/// global value". The prompt and tool-description columns are deliberately absent — no project is known at
/// <c>tools/list</c> time, so they stay global. Resolved by <c>IMcpSettingsProvider.GetEffectiveSettingsAsync</c>
/// as deployment lock → project value → global value → code default.
/// </summary>
public class McpProjectSettings : BaseEntity
{
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public int? MaxRowLimit { get; set; }
    public bool? EnforceReadOnly { get; set; }
    public bool? EnablePiiDetection { get; set; }
    public string? CustomPiiPatterns { get; set; }
    public bool? EnableSampleValueCollection { get; set; }

    // Learning settings
    public bool? EnableLearning { get; set; }
    public double? LearningAutoApproveThreshold { get; set; }
    public int? LearningInjectionBudgetChars { get; set; }
    public int? LearningSignalRetentionDays { get; set; }

    // Self-learning settings
    public bool? EnableSelfConsistency { get; set; }
    public int? SelfConsistencyCandidateCount { get; set; }
    public bool? EnableEvalJudge { get; set; }
    public bool? EnableSemanticRetrieval { get; set; }
    public int? ExemplarTopK { get; set; }

    // Replay-verification settings
    public bool? EnableReplayVerification { get; set; }
    public int? LearningReplayMinFlips { get; set; }

    // Knowledge-base Tier 3 settings
    public bool? EnableContextualRetrieval { get; set; }
    public int? DocChunkWindowSentences { get; set; }
    public int? DocChunkOverlapSentences { get; set; }
    public int? GlossaryTopK { get; set; }
    public int? DocChunkTopK { get; set; }

    // Golden-exemplar settings
    public bool? EnableGoldenExemplars { get; set; }
    public int? GoldenExemplarTopK { get; set; }
    public int? GoldenExemplarBudgetChars { get; set; }

    // Ask-correctness grounding settings
    public bool? EnableValueGrounding { get; set; }
    public int? ValueGroundingMaxProbes { get; set; }
    public bool? EnableSemanticLint { get; set; }
    public int? SelfConsistencyMinTables { get; set; }

    // Warehouse-engine settings (Wave 0.2 — stored here, enforced from Wave 1)
    public bool? RetainQueryContent { get; set; }
    public int? StatementTimeoutSeconds { get; set; }
    public int? MaxResultBytes { get; set; }
    public decimal? MaxExplainCost { get; set; }
    public int? MaxConcurrentQueriesPerKey { get; set; }
    public bool? AllowExplicitFeedbackContent { get; set; }
}

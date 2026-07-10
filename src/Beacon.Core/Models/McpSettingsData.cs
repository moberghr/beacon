namespace Beacon.Core.Models;

public class McpSettingsData
{
    public string? AskSystemPrompt { get; set; }
    public string? GlobalInstruction { get; set; }
    public string? GetContextDescription { get; set; }
    public string? QueryDescription { get; set; }
    public string? GetDocumentationDescription { get; set; }
    public string? AskDescription { get; set; }
    public string? SearchDescription { get; set; }
    public int MaxRowLimit { get; set; } = 1000;
    public bool EnforceReadOnly { get; set; } = true;
    public bool EnablePiiDetection { get; set; } = true;
    public List<string> CustomPiiPatterns { get; set; } = [];
    public bool EnableSampleValueCollection { get; set; } = true;

    // Learning settings
    public bool EnableLearning { get; set; } = true;
    public double LearningAutoApproveThreshold { get; set; } = 0.7;
    public int LearningInjectionBudgetChars { get; set; } = 1500;
    public int LearningSignalRetentionDays { get; set; } = 90;

    // Self-learning settings
    public bool EnableSelfConsistency { get; set; } = false;
    public int SelfConsistencyCandidateCount { get; set; } = 5;
    public bool EnableEvalJudge { get; set; } = false;
    public bool EnableSemanticRetrieval { get; set; } = true;
    public int ExemplarTopK { get; set; } = 4;

    // Replay-verification settings
    public bool EnableReplayVerification { get; set; } = true;
    public int LearningReplayMinFlips { get; set; } = 1;
}

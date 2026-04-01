using Semantico.Core.Data.Entities.Base;

namespace Semantico.Core.Data.Entities;

public class McpSettings : BaseEntity
{
    public string? AskSystemPrompt { get; set; }
    public string? GlobalInstruction { get; set; }
    public string? GetContextDescription { get; set; }
    public string? SearchDescription { get; set; }
    public string? QueryDescription { get; set; }
    public string? GetDocumentationDescription { get; set; }
    public string? AskDescription { get; set; }
    public int MaxRowLimit { get; set; } = 1000;
    public bool EnforceReadOnly { get; set; } = true;
    public bool EnablePiiDetection { get; set; } = true;
    public string? CustomPiiPatterns { get; set; }

    // Learning settings
    public bool EnableLearning { get; set; } = true;
    public double LearningAutoApproveThreshold { get; set; } = 0.7;
    public int LearningInjectionBudgetChars { get; set; } = 1500;
    public int LearningSignalRetentionDays { get; set; } = 90;
}

namespace Beacon.Core.Models.Ai;

public class AlertGenerationOptions
{
    public decimal Temperature { get; set; } = 0.3m;
    public int MaxTokens { get; set; } = 2048;
    public bool ValidateSyntax { get; set; } = true;
    public bool RequestClarification { get; set; } = true;
    public bool IncludeExplanation { get; set; } = true;
    public int MaxConversationTurns { get; set; } = 5;
    public string? PreferredModel { get; set; }
}

namespace Semantico.Core.Models.Ai;

public record LlmResponse
{
    public string Content { get; init; } = null!;
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int TotalTokens => InputTokens + OutputTokens;
    public decimal EstimatedCost { get; init; }
    public string Model { get; init; } = null!;
    public bool PromptCacheHit { get; init; }
}

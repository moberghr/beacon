namespace Semantico.Core.Models.Ai;

public record TokenUsageInfo
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int TotalTokens => InputTokens + OutputTokens;
    public decimal EstimatedCost { get; init; }
    public bool CacheHit { get; init; }
}

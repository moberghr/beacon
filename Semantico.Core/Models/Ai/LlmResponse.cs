namespace Semantico.Core.Models.Ai;

/// <summary>
/// Response from an LLM provider after completing a request.
/// This model is in Core to allow both Core and AI projects to use it as a shared contract.
/// </summary>
public record LlmResponse
{
    public required string Content { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public string Model { get; init; } = null!;
    public decimal Cost { get; init; }
    public bool PromptCacheHit { get; init; }
    
    public int TotalTokens => InputTokens + OutputTokens;
}

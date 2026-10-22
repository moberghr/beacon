using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;

namespace Beacon.AI.Services.LlmProviders;

public interface ILlmProvider
{
    Task<Beacon.Core.Models.Ai.LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);
    Task<TokenCount> CountTokensAsync(string text, CancellationToken cancellationToken = default);
}

public record LlmRequest
{
    public List<ChatMessage> Messages { get; init; } = new();
    public string? SystemPrompt { get; init; }
    public decimal Temperature { get; init; } = 0.3m;
    public int MaxTokens { get; init; } = 4096;

    /// <summary>Which configured model serves the request; <see cref="LlmTier.Fast"/> is for short classification calls.</summary>
    public LlmTier Tier { get; init; } = LlmTier.Default;

    /// <summary>
    /// What the completion is for, one of the <see cref="LlmOperations"/> constants; tags the completion's span and metrics.
    /// Never content.
    /// </summary>
    public string? Operation { get; init; }
}

public enum LlmTier
{
    Default,
    Fast
}

public record ChatMessage(ConversationRole Role, string Content)
{
    /// <summary>
    /// Marks the end of a reusable prompt prefix: the provider may cache everything up to and including this message
    /// (Claude gives it an ephemeral cache point). Set it on a long, repeated message such as one carrying the schema
    /// context. Providers that cache prompts on their own (OpenAI, Azure OpenAI) ignore it, and so does Bedrock, which does
    /// not request prompt caching yet.
    /// </summary>
    public bool CacheBreakpoint { get; init; }
}

public record TokenCount(int Tokens);

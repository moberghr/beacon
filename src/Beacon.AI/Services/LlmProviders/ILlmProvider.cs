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

    /// <summary>The request queue's lane: work done off a request (indexing, learning, generation jobs) is <see cref="LlmRequestPriority.Background"/>.</summary>
    public LlmRequestPriority Priority { get; init; } = LlmRequestPriority.Interactive;
}

/// <summary>
/// The lane a request waits in. Background requests hold at most half the queue's concurrency slots, so a burst of them
/// always leaves room for interactive calls.
/// </summary>
public enum LlmRequestPriority
{
    Interactive,
    Background
}

public record ChatMessage(ConversationRole Role, string Content);

public record TokenCount(int Tokens);

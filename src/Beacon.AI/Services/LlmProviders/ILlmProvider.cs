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
}

public record ChatMessage(ConversationRole Role, string Content);

public record TokenCount(int Tokens);

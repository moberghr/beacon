using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Ai;

namespace Semantico.Core.Services.LlmProviders;

public interface ILlmProvider
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);
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

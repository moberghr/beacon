using OpenAI;
using OpenAI.Chat;
using Beacon.Core.Data.Enums;

namespace Beacon.AI.Services.LlmProviders;

public class OpenAiProvider : ILlmProvider
{
    private readonly ChatClient _chatClient;
    private readonly string _model;
    private readonly Dictionary<string, (decimal input, decimal output)> _pricing = new()
    {
        ["gpt-4o"] = (5.00m, 15.00m),
        ["gpt-4o-mini"] = (0.15m, 0.60m),
        ["gpt-4-turbo"] = (10.00m, 30.00m),
        ["gpt-3.5-turbo"] = (0.50m, 1.50m)
    };

    public OpenAiProvider(string apiKey, string model = "gpt-4o-mini")
    {
        var client = new OpenAIClient(apiKey);
        _chatClient = client.GetChatClient(model);
        _model = model;
    }

    public async Task<Beacon.Core.Models.Ai.LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var messages = new List<OpenAI.Chat.ChatMessage>();

        // Add system message if provided
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new SystemChatMessage(request.SystemPrompt));
        }

        // Add conversation messages
        foreach (var msg in request.Messages)
        {
            messages.Add(msg.Role switch
            {
                ConversationRole.User => new UserChatMessage(msg.Content),
                ConversationRole.Assistant => new AssistantChatMessage(msg.Content),
                _ => new UserChatMessage(msg.Content)
            });
        }

        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = request.MaxTokens,
            Temperature = (float)request.Temperature
        };

        var response = await _chatClient.CompleteChatAsync(messages, options, cancellationToken);

        // Content is empty on a content-filter / refusal response — guard against ArgumentOutOfRangeException.
        // Mirrors ClaudeProvider / BedrockProvider.
        var content = response.Value.Content?.FirstOrDefault()?.Text ?? string.Empty;
        var usage = response.Value.Usage;

        // Calculate cost based on model pricing
        var (inputCostPerMillion, outputCostPerMillion) = GetPricing(_model);
        var inputCost = (usage.InputTokenCount / 1_000_000m) * inputCostPerMillion;
        var outputCost = (usage.OutputTokenCount / 1_000_000m) * outputCostPerMillion;

        return new Beacon.Core.Models.Ai.LlmResponse
        {
            Content = content,
            InputTokens = usage.InputTokenCount,
            OutputTokens = usage.OutputTokenCount,
            Cost = inputCost + outputCost,
            Model = _model,
            PromptCacheHit = false,
            Truncated = response.Value.FinishReason == ChatFinishReason.Length
        };
    }

    public Task<TokenCount> CountTokensAsync(string text, CancellationToken cancellationToken = default)
    {
        // Approximate token count using tiktoken-like estimation
        // ~4 characters per token as rough estimate
        var estimatedTokens = text.Length / 4;
        return Task.FromResult(new TokenCount(estimatedTokens));
    }

    private (decimal input, decimal output) GetPricing(string model)
    {
        // Try to find exact match or closest match
        foreach (var key in _pricing.Keys)
        {
            if (model.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                return _pricing[key];
            }
        }

        // Default to GPT-4o pricing if unknown
        return (5.00m, 15.00m);
    }
}

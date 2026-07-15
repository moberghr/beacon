using Azure;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using Beacon.Core.Data.Enums;

namespace Beacon.AI.Services.LlmProviders;

public class AzureOpenAiProvider : ILlmProvider
{
    private readonly ChatClient _chatClient;
    private readonly string _model;
    private const decimal InputCostPerMillionTokens = 5.00m;  // Same as OpenAI GPT-4o
    private const decimal OutputCostPerMillionTokens = 15.00m;

    public AzureOpenAiProvider(string endpoint, string apiKey, string deploymentName)
    {
        var client = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        _chatClient = client.GetChatClient(deploymentName);
        _model = deploymentName;
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

        // Calculate cost (Azure uses same pricing as OpenAI)
        var inputCost = (usage.InputTokenCount / 1_000_000m) * InputCostPerMillionTokens;
        var outputCost = (usage.OutputTokenCount / 1_000_000m) * OutputCostPerMillionTokens;

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
        // Approximate token count
        // ~4 characters per token as rough estimate
        var estimatedTokens = text.Length / 4;
        return Task.FromResult(new TokenCount(estimatedTokens));
    }
}

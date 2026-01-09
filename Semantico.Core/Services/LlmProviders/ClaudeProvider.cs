using System.Net.Http.Json;
using System.Text.Json;
using Semantico.Core.Data.Enums;
using Semantico.Core.Exceptions;

namespace Semantico.Core.Services.LlmProviders;

public class ClaudeProvider : ILlmProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private const decimal InputCostPerMillionTokens = 3.00m;
    private const decimal OutputCostPerMillionTokens = 15.00m;
    private const decimal CachedInputCostPerMillionTokens = 0.30m; // 90% discount

    public ClaudeProvider(string apiKey, string model = "claude-sonnet-4-20250514")
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentNullException(nameof(apiKey));
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentNullException(nameof(model));

        _apiKey = apiKey;
        _model = model;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.anthropic.com/v1/")
        };
        _httpClient.DefaultRequestHeaders.Add("x-api-key", _apiKey);
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public async Task<Models.Ai.LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var messages = request.Messages
                .Select(m => new
                {
                    role = m.Role == ConversationRole.User ? "user" : "assistant",
                    content = m.Content
                })
                .ToList();

            var requestBody = new
            {
                model = _model,
                messages,
                max_tokens = request.MaxTokens,
                temperature = request.Temperature,
                system = request.SystemPrompt
            };

            var response = await _httpClient.PostAsJsonAsync("messages", requestBody, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<ClaudeResponse>(responseContent);

            if (result == null)
                throw new AiServiceException("Failed to parse Claude API response");

            var content = result.Content?.FirstOrDefault()?.Text ?? string.Empty;

            // Calculate costs
            var inputCost = (result.Usage.InputTokens / 1_000_000m) * InputCostPerMillionTokens;
            var outputCost = (result.Usage.OutputTokens / 1_000_000m) * OutputCostPerMillionTokens;
            var cachedInputCost = result.Usage.CacheReadInputTokens > 0
                ? (result.Usage.CacheReadInputTokens / 1_000_000m) * CachedInputCostPerMillionTokens
                : 0;

            var totalCost = inputCost + outputCost + cachedInputCost;
            var promptCacheHit = result.Usage.CacheReadInputTokens > 0;

            return new Models.Ai.LlmResponse
            {
                Content = content,
                InputTokens = result.Usage.InputTokens,
                OutputTokens = result.Usage.OutputTokens,
                EstimatedCost = totalCost,
                Model = result.Model,
                PromptCacheHit = promptCacheHit
            };
        }
        catch (Exception ex) when (ex is not AiServiceException)
        {
            throw new AiServiceException(
                $"Claude API request failed: {ex.Message}",
                ex);
        }
    }

    private class ClaudeResponse
    {
        public string Model { get; set; } = null!;
        public List<ContentBlock>? Content { get; set; }
        public UsageInfo Usage { get; set; } = null!;
    }

    private class ContentBlock
    {
        public string Type { get; set; } = null!;
        public string Text { get; set; } = null!;
    }

    private class UsageInfo
    {
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int CacheReadInputTokens { get; set; }
    }

    public Task<TokenCount> CountTokensAsync(string text, CancellationToken cancellationToken = default)
    {
        // Approximate token count using Claude's estimation
        // ~4 characters per token as rough estimate
        var estimatedTokens = text.Length / 4;
        return Task.FromResult(new TokenCount(estimatedTokens));
    }
}

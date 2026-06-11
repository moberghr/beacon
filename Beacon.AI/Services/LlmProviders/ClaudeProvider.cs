using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Beacon.Core.Data.Enums;
using Beacon.Core.Exceptions;

namespace Beacon.AI.Services.LlmProviders;

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
            BaseAddress = new Uri("https://api.anthropic.com/v1/"),
            Timeout = TimeSpan.FromMinutes(5)
        };
        _httpClient.DefaultRequestHeaders.Add("x-api-key", _apiKey);
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public async Task<Beacon.Core.Models.Ai.LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
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

            if (result.Usage == null)
                throw new AiServiceException("Claude API response did not contain usage information");

            var content = result.Content?.FirstOrDefault()?.Text ?? string.Empty;

            // Calculate costs
            var inputCost = (result.Usage.InputTokens / 1_000_000m) * InputCostPerMillionTokens;
            var outputCost = (result.Usage.OutputTokens / 1_000_000m) * OutputCostPerMillionTokens;
            var cachedInputCost = result.Usage.CacheReadInputTokens > 0
                ? (result.Usage.CacheReadInputTokens / 1_000_000m) * CachedInputCostPerMillionTokens
                : 0;

            var totalCost = inputCost + outputCost + cachedInputCost;
            var promptCacheHit = result.Usage.CacheReadInputTokens > 0;

            return new Beacon.Core.Models.Ai.LlmResponse
            {
                Content = content,
                InputTokens = result.Usage.InputTokens,
                OutputTokens = result.Usage.OutputTokens,
                Cost = totalCost,
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
        [JsonPropertyName("model")]
        public string Model { get; set; } = null!;

        [JsonPropertyName("content")]
        public List<ContentBlock>? Content { get; set; }

        [JsonPropertyName("usage")]
        public UsageInfo? Usage { get; set; }
    }

    private class ContentBlock
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = null!;

        [JsonPropertyName("text")]
        public string Text { get; set; } = null!;
    }

    private class UsageInfo
    {
        [JsonPropertyName("input_tokens")]
        public int InputTokens { get; set; }

        [JsonPropertyName("output_tokens")]
        public int OutputTokens { get; set; }

        [JsonPropertyName("cache_read_input_tokens")]
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

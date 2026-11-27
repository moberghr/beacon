using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Beacon.Core.Data.Enums;
using Beacon.Core.Exceptions;
using Beacon.Core.Helpers;

namespace Beacon.AI.Services.LlmProviders;

public class ClaudeProvider : ILlmProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly TimeProvider _timeProvider;
    private const decimal DefaultInputCostPerMillionTokens = 3.00m;
    private const decimal DefaultOutputCostPerMillionTokens = 15.00m;

    // Per million tokens, matched by the model id containing the name. Any other model is costed at the default.
    private static readonly (string Name, decimal Input, decimal Output)[] ModelPricing =
    [
        ("claude-opus-5-5", 4.00m, 20.00m),
        ("claude-sonnet-5-5", 2.00m, 10.00m),
        ("claude-haiku-5-5", 0.10m, 0.50m),
        ("claude-fable-5-2", 8.00m, 40.00m)
    ];

    private const string AnthropicVersion = "2023-06-01";

    internal const long MaxResponseBytes = LlmHttpClients.MaxResponseBytes;

    private static readonly Uri MessagesUri = new("https://api.anthropic.com/v1/messages");

    public ClaudeProvider(string apiKey, string model = "claude-sonnet-5-5")
        : this(LlmHttpClients.Shared, apiKey, model)
    {
    }

    internal ClaudeProvider(HttpClient httpClient, string apiKey, string model, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentNullException(nameof(apiKey));
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentNullException(nameof(model));

        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = apiKey;
        _model = model;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The model the client was built for.</summary>
    internal string Model => _model;

    public async Task<Beacon.Core.Models.Ai.LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var messages = request.Messages
                .Select(m => new
                {
                    role = m.Role == ConversationRole.User ? "user" : "assistant",
                    content = MessageContent(m)
                })
                .ToList();

            var requestBody = new Dictionary<string, object?>
            {
                ["model"] = _model,
                ["messages"] = messages,
                ["max_tokens"] = request.MaxTokens
            };

            // A request without a system prompt (the connection test) gets no system field: the API refuses a null one.
            if (!string.IsNullOrEmpty(request.SystemPrompt))
            {
                requestBody["system"] = request.SystemPrompt;
            }

            // A model that refuses sampling parameters gets no temperature at all, not a null one (ClaudeSampling).
            if (ClaudeSampling.AcceptsTemperature(_model))
            {
                requestBody["temperature"] = request.Temperature;
            }

            // Headers are set per request, never on the client: the client may be shared.
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, MessagesUri)
            {
                Content = JsonContent.Create(requestBody)
            };
            httpRequest.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
            httpRequest.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);

            // The body is read under the client's own deadline, as the headers are, and only up to MaxResponseBytes.
            using var deadline = new CancellationTokenSource(_httpClient.Timeout, _timeProvider);
            using var call = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, call.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw StatusFailure(response);
            }

            var responseContent = await LlmHttpClients.ReadCappedAsync(response.Content, MaxResponseBytes, call.Token)
                ?? throw new AiServiceException($"Claude API returned a response larger than {MaxResponseBytes} bytes.");
            var result = JsonSerializer.Deserialize<ClaudeResponse>(responseContent);

            if (result == null)
                throw new AiServiceException("Failed to parse Claude API response");

            if (result.Usage == null)
                throw new AiServiceException("Claude API response did not contain usage information");

            var content = ClaudeResponseText.Of(result.Content?.Select(x => (x.Type, x.Text)));

            // Calculate costs. A cache read costs a tenth of the input price, a cache write a quarter more.
            var (inputCostPerMillion, outputCostPerMillion) = Pricing(_model);
            var inputCost = (result.Usage.InputTokens / 1_000_000m) * inputCostPerMillion;
            var outputCost = (result.Usage.OutputTokens / 1_000_000m) * outputCostPerMillion;
            // The cache counts are absent (or null) when the request asked for no caching.
            var cacheReadTokens = result.Usage.CacheReadInputTokens ?? 0;
            var cacheWriteTokens = result.Usage.CacheCreationInputTokens ?? 0;
            var cachedInputCost = cacheReadTokens > 0
                ? (cacheReadTokens / 1_000_000m) * inputCostPerMillion * 0.1m
                : 0;

            var cacheWriteCost = (cacheWriteTokens / 1_000_000m) * inputCostPerMillion * 1.25m;

            var totalCost = inputCost + outputCost + cachedInputCost + cacheWriteCost;
            var promptCacheHit = cacheReadTokens > 0;

            return new Beacon.Core.Models.Ai.LlmResponse
            {
                Content = content,
                InputTokens = result.Usage.InputTokens,
                OutputTokens = result.Usage.OutputTokens,
                Cost = totalCost,
                Model = result.Model,
                PromptCacheHit = promptCacheHit,
                Truncated = result.StopReason == "max_tokens",
                CacheReadInputTokens = cacheReadTokens,
                CacheWriteInputTokens = cacheWriteTokens
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // The caller's token is not cancelled, so the client's own timeout ended the request.
            throw new LlmTransientException("Claude API did not answer in time.", null, ex);
        }
        catch (Exception ex) when (ex is HttpRequestException { StatusCode: null } or IOException)
        {
            throw new LlmTransientException("Claude API could not be reached.", null, ex);
        }
        catch (Exception ex) when (ex is not AiServiceException)
        {
            // Beacon's wording and the failure's type and code only: a message can quote the response.
            throw new AiServiceException($"Claude API request failed ({FailureCodes.Describe(ex)}).", ex);
        }
    }

    // This provider does not retry: the request queue retries a transient failure (408, 429, 5xx and Anthropic's 529
    // overloaded), waiting as long as retry-after asks. The failure names the status only, never the response body.
    private AiServiceException StatusFailure(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        var message = $"Claude API returned HTTP {status}.";
        if (status is not (408 or 429 or >= 500 and <= 599))
        {
            return new AiServiceException(message);
        }

        return new LlmTransientException(message, RetryAfter(response));
    }

    // An HTTP date is measured against the provider's clock; a date already past asks for no wait.
    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var wait = date - _timeProvider.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private static (decimal Input, decimal Output) Pricing(string model)
    {
        foreach (var (name, input, output) in ModelPricing)
        {
            if (model.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return (input, output);
            }
        }

        return (DefaultInputCostPerMillionTokens, DefaultOutputCostPerMillionTokens);
    }

    private class ClaudeResponse
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = null!;

        [JsonPropertyName("content")]
        public List<ContentBlock>? Content { get; set; }

        [JsonPropertyName("usage")]
        public UsageInfo? Usage { get; set; }

        [JsonPropertyName("stop_reason")]
        public string? StopReason { get; set; }
    }

    private class ContentBlock
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    private class UsageInfo
    {
        [JsonPropertyName("input_tokens")]
        public int InputTokens { get; set; }

        [JsonPropertyName("output_tokens")]
        public int OutputTokens { get; set; }

        [JsonPropertyName("cache_read_input_tokens")]
        public int? CacheReadInputTokens { get; set; }

        [JsonPropertyName("cache_creation_input_tokens")]
        public int? CacheCreationInputTokens { get; set; }
    }

    // A message marked as a cache breakpoint goes as one text block carrying an ephemeral cache point (the prompt up to and
    // including it becomes a cacheable prefix); any other message keeps the plain string it always was.
    private static object MessageContent(ChatMessage message)
    {
        if (!message.CacheBreakpoint)
        {
            return message.Content;
        }

        return new[]
        {
            new
            {
                type = "text",
                text = message.Content,
                cache_control = new { type = "ephemeral" }
            }
        };
    }

    public Task<TokenCount> CountTokensAsync(string text, CancellationToken cancellationToken = default)
    {
        // Approximate token count using Claude's estimation
        // ~4 characters per token as rough estimate
        var estimatedTokens = text.Length / 4;
        return Task.FromResult(new TokenCount(estimatedTokens));
    }
}

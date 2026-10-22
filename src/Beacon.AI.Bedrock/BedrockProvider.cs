using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Beacon.Core.Data.Enums;
using Beacon.Core.Exceptions;

namespace Beacon.AI.Services.LlmProviders;

public class BedrockProvider : ILlmProvider
{
    private readonly IAmazonBedrockRuntime _client;
    private readonly string _modelId;
    private readonly Amazon.Runtime.AWSCredentials? _explicitCredentials;
    private readonly Dictionary<string, (decimal input, decimal output)> _pricing = new()
    {
        // Claude Sonnet 4.5 pricing (per million tokens)
        ["anthropic.claude-sonnet-4-5"] = (3.00m, 15.00m),
        ["us.anthropic.claude-sonnet-4-5"] = (3.00m, 15.00m),
        ["eu.anthropic.claude-sonnet-4-5"] = (3.00m, 15.00m),

        // Claude 3.5 Sonnet pricing (per million tokens)
        ["anthropic.claude-3-5-sonnet"] = (3.00m, 15.00m),
        ["us.anthropic.claude-3-5-sonnet"] = (3.00m, 15.00m),

        // Claude 3.5 Haiku pricing
        ["anthropic.claude-3-5-haiku"] = (0.80m, 4.00m),
        ["us.anthropic.claude-3-5-haiku"] = (0.80m, 4.00m),

        // Claude 3 Opus pricing
        ["anthropic.claude-3-opus"] = (15.00m, 75.00m),
        ["us.anthropic.claude-3-opus"] = (15.00m, 75.00m),

        // Claude 3 Sonnet pricing
        ["anthropic.claude-3-sonnet"] = (3.00m, 15.00m),
        ["us.anthropic.claude-3-sonnet"] = (3.00m, 15.00m),

        // Claude 3 Haiku pricing
        ["anthropic.claude-3-haiku"] = (0.25m, 1.25m),
        ["us.anthropic.claude-3-haiku"] = (0.25m, 1.25m),

        // Meta Llama 3.1 pricing
        ["meta.llama3-1-405b"] = (2.65m, 3.50m),
        ["meta.llama3-1-70b"] = (0.99m, 0.99m),
        ["meta.llama3-1-8b"] = (0.22m, 0.22m)
    };

    public BedrockProvider(string region, string modelId)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(region);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(modelId);

        _client = CreateClient(null, CreateConfig(region));
        _modelId = modelId;
    }

    public BedrockProvider(string accessKey, string secretKey, string region, string modelId)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(accessKey);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(secretKey);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(region);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(modelId);

        _explicitCredentials = new Amazon.Runtime.BasicAWSCredentials(accessKey, secretKey);
        _client = CreateClient(_explicitCredentials, CreateConfig(region));
        _modelId = modelId;
    }

    public BedrockProvider(string accessKey, string secretKey, string sessionToken, string region, string modelId)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(accessKey);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(secretKey);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(sessionToken);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(region);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(modelId);

        try
        {
            _explicitCredentials = new Amazon.Runtime.SessionAWSCredentials(accessKey, secretKey, sessionToken);
            _client = CreateClient(_explicitCredentials, CreateConfig(region));
            _modelId = modelId;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to initialize BedrockProvider: {ex.Message}", ex);
        }
    }

    internal BedrockProvider(IAmazonBedrockRuntime client, string modelId)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(modelId);

        _client = client ?? throw new ArgumentNullException(nameof(client));
        _modelId = modelId;
    }

    /// <summary>The static credentials the client was built with; null when it uses the default AWS credential chain.</summary>
    internal Amazon.Runtime.AWSCredentials? ExplicitCredentials => _explicitCredentials;

    /// <summary>The model id the client was built for.</summary>
    internal string ModelId => _modelId;

    // The SDK does not retry (MaxErrorRetry = 0): the request queue is the one retry layer, and TransientFailure tells it
    // which failures may pass. The client's retry policy (ClockSkewRetryPolicy, see CreateClient) only re-signs and sends
    // once more a request refused because the local clock is off.
    internal static AmazonBedrockRuntimeConfig CreateConfig(string region)
    {
        var regionEndpoint = RegionEndpoint.GetBySystemName(region)
            ?? throw new InvalidOperationException($"Invalid AWS region: {region}");

        return new AmazonBedrockRuntimeConfig
        {
            RegionEndpoint = regionEndpoint,
            Timeout = TimeSpan.FromMinutes(5),
            MaxErrorRetry = 0
        };
    }

    internal static IAmazonBedrockRuntime CreateClient(Amazon.Runtime.AWSCredentials? credentials, AmazonBedrockRuntimeConfig config)
    {
        return credentials == null
            ? new ClockSkewCorrectingBedrockClient(config)
            : new ClockSkewCorrectingBedrockClient(credentials, config);
    }

    /// <summary>
    /// The failure as one the request queue retries, or null when it is not one: throttling, an unavailable service, a
    /// model that is not ready or timed out, an internal error, any service failure with status 408, 429 or 5xx, and a
    /// connection failure or client timeout may pass. With its retries off the SDK reports a connection failure as an
    /// <see cref="HttpRequestException"/> without a status or as the <see cref="IOException"/> inside it, and its client
    /// timeout as a <see cref="TimeoutException"/> (or a cancellation while the caller's token is not cancelled). The
    /// message names the failure's type only, never the service's text.
    /// </summary>
    internal static LlmTransientException? TransientFailure(Exception exception)
    {
        return exception switch
        {
            ThrottlingException
                or ServiceUnavailableException
                or ModelTimeoutException
                or ModelNotReadyException
                or InternalServerException
                or Amazon.Runtime.AmazonServiceException { StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError and <= (HttpStatusCode)599 }
                or HttpRequestException { StatusCode: null }
                or IOException and not FileNotFoundException =>
                new LlmTransientException($"AWS Bedrock request failed and may be retried ({exception.GetType().Name}).", null, exception),
            OperationCanceledException or TimeoutException =>
                new LlmTransientException("Bedrock request timed out. The AI model took too long to respond.", null, exception),
            _ => null
        };
    }

    public async Task<Beacon.Core.Models.Ai.LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        if (_client == null)
            throw new AiServiceException("Bedrock client is not initialized.");

        if (request == null)
            throw new ArgumentNullException(nameof(request));

        if (request.Messages == null || !request.Messages.Any())
            throw new ArgumentException("Request must contain at least one message", nameof(request));

        try
        {
            if (_modelId.Contains("claude", StringComparison.OrdinalIgnoreCase))
                return await InvokeClaudeModelAsync(request, cancellationToken);

            if (_modelId.Contains("llama", StringComparison.OrdinalIgnoreCase))
                return await InvokeLlamaModelAsync(request, cancellationToken);

            throw new AiServiceException($"Model {_modelId} is not supported by BedrockProvider");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (TransientFailure(ex) is { } transient)
        {
            throw transient;
        }
        catch (Exception ex) when (ex is not AiServiceException)
        {
            throw new AiServiceException($"AWS Bedrock API request failed: {ex.Message}", ex);
        }
    }

    private async Task<Beacon.Core.Models.Ai.LlmResponse> InvokeClaudeModelAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        try
        {
            // Build Claude-specific request format
            var messages = request.Messages.Select(m => new
            {
                role = m.Role == Beacon.Core.Data.Enums.ConversationRole.User ? "user" : "assistant",
                content = m.Content
            }).ToList();

            var requestBody = new
            {
                anthropic_version = "bedrock-2023-05-31",
                max_tokens = request.MaxTokens,
                temperature = request.Temperature,
                system = request.SystemPrompt ?? string.Empty,
                messages
            };

            var jsonRequest = JsonSerializer.Serialize(requestBody);

            var invokeRequest = new InvokeModelRequest
            {
                ModelId = _modelId,
                ContentType = "application/json",
                Accept = "application/json",
                Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(jsonRequest))
            };

            var response = await _client.InvokeModelAsync(invokeRequest, cancellationToken);

            using var reader = new StreamReader(response.Body);
            var responseBody = await reader.ReadToEndAsync(cancellationToken);

            return ParseClaudeResponse(responseBody);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (TransientFailure(ex) is { } transient)
        {
            throw transient;
        }
        catch (Amazon.BedrockRuntime.Model.ValidationException vex)
        {
            throw new AiServiceException($"Bedrock validation error: {vex.Message}", vex);
        }
        catch (Amazon.Runtime.AmazonServiceException asex)
        {
            throw new AiServiceException($"AWS Bedrock service error: {asex.Message} (Status: {asex.StatusCode})", asex);
        }
        catch (Exception ex)
        {
            throw new AiServiceException($"Bedrock request failed: {ex.Message}", ex);
        }
    }

    private async Task<Beacon.Core.Models.Ai.LlmResponse> InvokeLlamaModelAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        // Build Llama-specific request format
        var prompt = new System.Text.StringBuilder();

        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            prompt.AppendLine($"<|begin_of_text|><|start_header_id|>system<|end_header_id|>");
            prompt.AppendLine(request.SystemPrompt);
            prompt.AppendLine("<|eot_id|>");
        }

        foreach (var message in request.Messages)
        {
            var role = message.Role == Beacon.Core.Data.Enums.ConversationRole.User ? "user" : "assistant";
            prompt.AppendLine($"<|start_header_id|>{role}<|end_header_id|>");
            prompt.AppendLine(message.Content);
            prompt.AppendLine("<|eot_id|>");
        }

        prompt.AppendLine("<|start_header_id|>assistant<|end_header_id|>");

        var requestBody = new
        {
            prompt = prompt.ToString(),
            max_gen_len = request.MaxTokens,
            temperature = request.Temperature
        };

        var jsonRequest = JsonSerializer.Serialize(requestBody);

        var invokeRequest = new InvokeModelRequest
        {
            ModelId = _modelId,
            ContentType = "application/json",
            Accept = "application/json",
            Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(jsonRequest))
        };

        var response = await _client.InvokeModelAsync(invokeRequest, cancellationToken);

        using var reader = new StreamReader(response.Body);
        var responseBody = await reader.ReadToEndAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<LlamaBedrockResponse>(responseBody,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (result == null)
            throw new AiServiceException("Failed to parse Bedrock Llama API response");

        if (result.Generation == null)
            throw new AiServiceException("Bedrock Llama API response did not contain a generation");

        // Calculate costs (approximate token counts)
        var inputTokens = prompt.Length / 4; // Rough estimate
        var outputTokens = result.Generation.Length / 4; // Rough estimate

        var (inputCostPerMillion, outputCostPerMillion) = GetPricing(_modelId);
        var inputCost = (inputTokens / 1_000_000m) * inputCostPerMillion;
        var outputCost = (outputTokens / 1_000_000m) * outputCostPerMillion;

        return new Beacon.Core.Models.Ai.LlmResponse
        {
            Content = result.Generation,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Cost = inputCost + outputCost,
            Model = _modelId,
            PromptCacheHit = false,
            Truncated = result.StopReason == "length"
        };
    }

    public Task<TokenCount> CountTokensAsync(string text, CancellationToken cancellationToken = default)
    {
        // Approximate token count
        // ~4 characters per token as rough estimate
        var estimatedTokens = text.Length / 4;
        return Task.FromResult(new TokenCount(estimatedTokens));
    }

    /// <summary>
    /// The response of a Claude model on Bedrock: content, token counts (prompt cache read and write included, null-safe),
    /// cost at this model's prices and the truncation flag. Bedrock is sent no cache point yet, but a response that reports
    /// cache tokens is costed at Anthropic's cache rates.
    /// </summary>
    internal Beacon.Core.Models.Ai.LlmResponse ParseClaudeResponse(string responseBody)
    {
        var result = JsonSerializer.Deserialize<ClaudeBedrockResponse>(responseBody,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (result == null)
        {
            throw new AiServiceException("Failed to parse Bedrock Claude API response");
        }

        var content = result.Content?.FirstOrDefault()?.Text ?? string.Empty;

        // Calculate costs
        var (inputCostPerMillion, outputCostPerMillion) = GetPricing(_modelId);
        var inputCost = (result.Usage?.InputTokens / 1_000_000m) * inputCostPerMillion;
        var outputCost = (result.Usage?.OutputTokens / 1_000_000m) * outputCostPerMillion;
        var cacheReadTokens = result.Usage?.CacheReadInputTokens ?? 0;
        var cacheWriteTokens = result.Usage?.CacheCreationInputTokens ?? 0;

        // Anthropic's cache rates: a read at a tenth of the input price, a write at a quarter more.
        var cacheCost = (cacheReadTokens / 1_000_000m) * inputCostPerMillion * 0.1m
            + (cacheWriteTokens / 1_000_000m) * inputCostPerMillion * 1.25m;

        return new Beacon.Core.Models.Ai.LlmResponse
        {
            Content = content,
            InputTokens = result.Usage?.InputTokens ?? 0,
            OutputTokens = result.Usage?.OutputTokens ?? 0,
            Cost = (inputCost ?? 0) + (outputCost ?? 0) + cacheCost,
            Model = _modelId,
            PromptCacheHit = cacheReadTokens > 0,
            Truncated = result.StopReason == "max_tokens",
            CacheReadInputTokens = cacheReadTokens,
            CacheWriteInputTokens = cacheWriteTokens
        };
    }

    private (decimal input, decimal output) GetPricing(string modelId)
    {
        // Try to find exact match or closest match
        foreach (var key in _pricing.Keys)
        {
            if (modelId.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                return _pricing[key];
            }
        }

        // Default to Claude 3.5 Sonnet pricing if unknown
        return (3.00m, 15.00m);
    }

    // Response models for Claude
    private class ClaudeBedrockResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = null!;

        [JsonPropertyName("type")]
        public string Type { get; set; } = null!;

        [JsonPropertyName("role")]
        public string Role { get; set; } = null!;

        [JsonPropertyName("content")]
        public List<ContentBlock>? Content { get; set; }

        [JsonPropertyName("model")]
        public string Model { get; set; } = null!;

        [JsonPropertyName("usage")]
        public UsageInfo Usage { get; set; } = null!;

        [JsonPropertyName("stop_reason")]
        public string? StopReason { get; set; }
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
        public int? CacheReadInputTokens { get; set; }

        [JsonPropertyName("cache_creation_input_tokens")]
        public int? CacheCreationInputTokens { get; set; }
    }

    // Response models for Llama
    private class LlamaBedrockResponse
    {
        [JsonPropertyName("generation")]
        public string Generation { get; set; } = null!;

        [JsonPropertyName("prompt_token_count")]
        public int PromptTokenCount { get; set; }

        [JsonPropertyName("generation_token_count")]
        public int GenerationTokenCount { get; set; }

        [JsonPropertyName("stop_reason")]
        public string? StopReason { get; set; }
    }
}

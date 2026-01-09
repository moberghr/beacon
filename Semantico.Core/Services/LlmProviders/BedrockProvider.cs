using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Semantico.Core.Data.Enums;
using Semantico.Core.Exceptions;

namespace Semantico.Core.Services.LlmProviders;

public class BedrockProvider : ILlmProvider
{
    private readonly AmazonBedrockRuntimeClient _client;
    private readonly string _modelId;
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

        var config = CreateConfig(region);
        _client = new AmazonBedrockRuntimeClient(config);
        _modelId = modelId;
    }

    public BedrockProvider(string accessKey, string secretKey, string region, string modelId)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(accessKey);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(secretKey);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(region);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(modelId);

        var config = CreateConfig(region);
        _client = new AmazonBedrockRuntimeClient(accessKey, secretKey, config);
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
            var config = CreateConfig(region);
            var credentials = new Amazon.Runtime.SessionAWSCredentials(accessKey, secretKey, sessionToken);
            _client = new AmazonBedrockRuntimeClient(credentials, config);
            _modelId = modelId;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to initialize BedrockProvider: {ex.Message}", ex);
        }
    }

    private static AmazonBedrockRuntimeConfig CreateConfig(string region)
    {
        var regionEndpoint = RegionEndpoint.GetBySystemName(region)
            ?? throw new InvalidOperationException($"Invalid AWS region: {region}");

        return new AmazonBedrockRuntimeConfig
        {
            RegionEndpoint = regionEndpoint,
            Timeout = TimeSpan.FromMinutes(5),
            MaxErrorRetry = 3
        };
    }

    public async Task<Models.Ai.LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
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
        catch (Exception ex) when (ex is not AiServiceException)
        {
            throw new AiServiceException($"AWS Bedrock API request failed: {ex.Message}", ex);
        }
    }

    private async Task<Models.Ai.LlmResponse> InvokeClaudeModelAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        try
        {
            // Build Claude-specific request format
            var messages = request.Messages.Select(m => new
            {
                role = m.Role == Data.Enums.ConversationRole.User ? "user" : "assistant",
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

            var result = JsonSerializer.Deserialize<ClaudeBedrockResponse>(responseBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (result == null)
                throw new AiServiceException("Failed to parse Bedrock Claude API response");

            var content = result.Content?.FirstOrDefault()?.Text ?? string.Empty;

            // Calculate costs
            var (inputCostPerMillion, outputCostPerMillion) = GetPricing(_modelId);
            var inputCost = (result.Usage?.InputTokens / 1_000_000m) * inputCostPerMillion;
            var outputCost = (result.Usage?.OutputTokens / 1_000_000m) * outputCostPerMillion;

            return new Models.Ai.LlmResponse
            {
                Content = content,
                InputTokens = result.Usage?.InputTokens ?? 0,
                OutputTokens = result.Usage?.OutputTokens ?? 0,
                EstimatedCost = inputCost ?? 0 + outputCost ?? 0,
                Model = _modelId,
                PromptCacheHit = false
            };
        }
        catch (Amazon.BedrockRuntime.Model.ValidationException vex)
        {
            throw new AiServiceException($"Bedrock validation error: {vex.Message}", vex);
        }
        catch (Amazon.Runtime.AmazonServiceException asex)
        {
            throw new AiServiceException($"AWS Bedrock service error: {asex.Message} (Status: {asex.StatusCode})", asex);
        }
        catch (TaskCanceledException tcex)
        {
            throw new AiServiceException("Bedrock request timed out. The AI model took too long to respond.", tcex);
        }
        catch (Exception ex)
        {
            throw new AiServiceException($"Bedrock request failed: {ex.Message}", ex);
        }
    }

    private async Task<Models.Ai.LlmResponse> InvokeLlamaModelAsync(LlmRequest request, CancellationToken cancellationToken)
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
            var role = message.Role == Data.Enums.ConversationRole.User ? "user" : "assistant";
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
        var result = JsonSerializer.Deserialize<LlamaBedrockResponse>(responseBody);

        if (result == null)
            throw new AiServiceException("Failed to parse Bedrock Llama API response");

        // Calculate costs (approximate token counts)
        var inputTokens = prompt.Length / 4; // Rough estimate
        var outputTokens = result.Generation.Length / 4; // Rough estimate

        var (inputCostPerMillion, outputCostPerMillion) = GetPricing(_modelId);
        var inputCost = (inputTokens / 1_000_000m) * inputCostPerMillion;
        var outputCost = (outputTokens / 1_000_000m) * outputCostPerMillion;

        return new Models.Ai.LlmResponse
        {
            Content = result.Generation,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            EstimatedCost = inputCost + outputCost,
            Model = _modelId,
            PromptCacheHit = false
        };
    }

    public Task<TokenCount> CountTokensAsync(string text, CancellationToken cancellationToken = default)
    {
        // Approximate token count
        // ~4 characters per token as rough estimate
        var estimatedTokens = text.Length / 4;
        return Task.FromResult(new TokenCount(estimatedTokens));
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
    }

    // Response models for Llama
    private class LlamaBedrockResponse
    {
        public string Generation { get; set; } = null!;
        public string PromptTokenCount { get; set; } = null!;
        public string GenerationTokenCount { get; set; } = null!;
        public string StopReason { get; set; } = null!;
    }
}

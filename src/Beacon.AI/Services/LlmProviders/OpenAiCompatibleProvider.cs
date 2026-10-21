using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Beacon.Core.Data.Enums;
using Beacon.Core.Exceptions;

namespace Beacon.AI.Services.LlmProviders;

/// <summary>
/// The chat-completions API of OpenAI and of Azure OpenAI deployments, over plain HTTP. Sends the request the vendor
/// SDKs sent before (OpenAI: <c>max_completion_tokens</c>; Azure: <c>max_tokens</c> at a pinned API version) and
/// retries like their default policy: up to <see cref="MaxRetries"/> times on 408, 429, 5xx and transport failures,
/// waiting as the service asks (<c>retry-after-ms</c> / <c>Retry-After</c>, up to <see cref="MaxRetryAfter"/>) or with
/// exponential backoff. Each attempt, sending and reading the body, must finish within the attempt timeout (by default
/// the clients' <see cref="LlmHttpClients.Timeout"/>); one that does not fails like an <see cref="HttpClient"/> timeout
/// and is not retried here. A failure names the provider and the HTTP status only: never the response body, the
/// request or the key.
/// </summary>
internal sealed partial class OpenAiCompatibleProvider : ILlmProvider
{
    /// <summary>The Azure OpenAI API version: the default of the Azure SDK Beacon used before (Azure.AI.OpenAI 2.1.0).</summary>
    internal const string AzureApiVersion = "2024-10-21";

    internal const int MaxRetries = 3;

    internal const long MaxResponseBytes = 8 * 1024 * 1024;

    internal static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    private const long MaxErrorBytes = 64 * 1024;

    private const decimal DefaultInputCostPerMillionTokens = 5.00m;

    private const decimal DefaultOutputCostPerMillionTokens = 15.00m;

    private static readonly Uri OpenAiChatCompletionsUri = new("https://api.openai.com/v1/chat/completions");

    private static readonly TimeSpan BaseRetryDelay = TimeSpan.FromMilliseconds(800);

    // Matched by prefix in this order.
    private static readonly (string Prefix, decimal Input, decimal Output)[] OpenAiPricing =
    [
        ("gpt-4o", 5.00m, 15.00m),
        ("gpt-4o-mini", 0.15m, 0.60m),
        ("gpt-4-turbo", 10.00m, 30.00m),
        ("gpt-3.5-turbo", 0.50m, 1.50m)
    ];

    private readonly HttpClient _httpClient;
    private readonly bool _isAzure;
    private readonly Uri _requestUri;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _attemptTimeout;

    private OpenAiCompatibleProvider(
        HttpClient httpClient,
        bool isAzure,
        Uri requestUri,
        string apiKey,
        string model,
        Func<TimeSpan, CancellationToken, Task>? delay,
        TimeProvider? timeProvider,
        TimeSpan? attemptTimeout)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrEmpty(apiKey);
        ArgumentException.ThrowIfNullOrEmpty(model);

        _httpClient = httpClient;
        _isAzure = isAzure;
        _requestUri = requestUri;
        _apiKey = apiKey;
        _model = model;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _attemptTimeout = attemptTimeout ?? LlmHttpClients.Timeout;
        _delay = delay ?? ((wait, cancellationToken) => Task.Delay(wait, _timeProvider, cancellationToken));
    }

    /// <summary>The model (OpenAI) or deployment (Azure) the provider was built for.</summary>
    internal string Model => _model;

    internal bool IsAzure => _isAzure;

    internal Uri RequestUri => _requestUri;

    private string DisplayName => _isAzure ? "Azure OpenAI" : "OpenAI";

    public static OpenAiCompatibleProvider ForOpenAi(
        HttpClient httpClient,
        string apiKey,
        string model,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? timeProvider = null,
        TimeSpan? attemptTimeout = null)
    {
        return new OpenAiCompatibleProvider(httpClient, isAzure: false, OpenAiChatCompletionsUri, apiKey, model, delay, timeProvider, attemptTimeout);
    }

    public static OpenAiCompatibleProvider ForAzure(
        HttpClient httpClient,
        string endpoint,
        string apiKey,
        string deployment,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? timeProvider = null,
        TimeSpan? attemptTimeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(deployment);

        // Escaping leaves a dot segment as it is, and the URI would then drop or climb a path segment.
        if (deployment is "." or "..")
        {
            throw new ArgumentException("The Azure OpenAI deployment name cannot be a dot segment.", nameof(deployment));
        }

        return new OpenAiCompatibleProvider(
            httpClient,
            isAzure: true,
            AzureChatCompletionsUri(endpoint, deployment),
            apiKey,
            deployment,
            delay,
            timeProvider,
            attemptTimeout);
    }

    public async Task<Beacon.Core.Models.Ai.LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var body = RequestBody(request);

        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Attempt result;
            try
            {
                result = await SendOnceAsync(body, cancellationToken);
            }
            catch (Exception ex) when (attempt < MaxRetries && IsTransportFailure(ex) && !cancellationToken.IsCancellationRequested)
            {
                await _delay(BackoffDelay(attempt), cancellationToken);
                continue;
            }

            if (result.Succeeded)
            {
                return Completion(result.Body);
            }

            if (attempt < MaxRetries && IsRetryableStatus(result.Status) && RetryDelay(result.RequestedDelay, attempt) is { } delay)
            {
                await _delay(delay, cancellationToken);
                continue;
            }

            throw new HttpRequestException(FailureMessage(result.Status, result.ErrorCode), null, result.Status);
        }
    }

    public Task<TokenCount> CountTokensAsync(string text, CancellationToken cancellationToken = default)
    {
        // ~4 characters per token as rough estimate
        var estimatedTokens = text.Length / 4;
        return Task.FromResult(new TokenCount(estimatedTokens));
    }

    internal static bool IsRetryableStatus(HttpStatusCode status)
    {
        return status is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
    }

    private static Uri AzureChatCompletionsUri(string endpoint, string deployment)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);

        var endpointUri = new Uri(endpoint);
        if (endpointUri.Scheme != Uri.UriSchemeHttps && endpointUri.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException("The Azure OpenAI endpoint must be an absolute http or https URI.", nameof(endpoint));
        }

        // Credentials in the endpoint would travel in every request URI; the key goes in its own header.
        if (endpointUri.UserInfo.Length > 0)
        {
            throw new ArgumentException("The Azure OpenAI endpoint must not contain user information.", nameof(endpoint));
        }

        var basePath = endpointUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var query = endpointUri.Query.Length > 1 ? $"{endpointUri.Query}&" : "?";

        return new Uri($"{basePath}/openai/deployments/{Uri.EscapeDataString(deployment)}/chat/completions{query}api-version={AzureApiVersion}");
    }

    private static bool IsTransportFailure(Exception exception)
    {
        return exception is HttpRequestException { StatusCode: null } or IOException;
    }

    private static TimeSpan BackoffDelay(int attempt)
    {
        var jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
        return BaseRetryDelay * (1 << attempt) * jitter;
    }

    // The wait the service asked for, or backoff when it asked for none; null when it asked for longer than the cap.
    private static TimeSpan? RetryDelay(TimeSpan? requested, int attempt)
    {
        if (requested is null)
        {
            return BackoffDelay(attempt);
        }

        return requested <= MaxRetryAfter ? requested : null;
    }

    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, long maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maxBytes)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string ContentOf(JsonElement choice)
    {
        if (choice.ValueKind != JsonValueKind.Object
            || !choice.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        // Content parts: the SDK read the first part's text.
        if (content.ValueKind == JsonValueKind.Array
            && content.GetArrayLength() > 0
            && content[0].ValueKind == JsonValueKind.Object
            && content[0].TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String)
        {
            return text.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static int TokenCountOf(JsonElement usage, string name)
    {
        return usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count)
            ? count
            : 0;
    }

    // One attempt under its own deadline: HttpClient's timeout stops at the response headers, so the body read needs one too.
    private async Task<Attempt> SendOnceAsync(byte[] body, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_attemptTimeout, _timeProvider);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await SendAndReadAsync(body, attempt.Token);
        }
        catch (Exception ex) when ((ex is OperationCanceledException or IOException or HttpRequestException)
            && timeout.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            throw new TaskCanceledException(
                $"{DisplayName} did not answer within {_attemptTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds.",
                new TimeoutException("The attempt timeout elapsed."));
        }
    }

    private async Task<Attempt> SendAndReadAsync(byte[] body, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(body);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var completion = await ReadCappedAsync(response.Content, MaxResponseBytes, cancellationToken)
                ?? throw new AiServiceException($"{DisplayName} returned a response larger than {MaxResponseBytes} bytes.");

            return new Attempt(response.StatusCode, completion, null, null);
        }

        return new Attempt(response.StatusCode, null, RequestedDelay(response), await ErrorCodeAsync(response.Content, cancellationToken));
    }

    private HttpRequestMessage CreateRequest(byte[] body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _requestUri)
        {
            Content = new ByteArrayContent(body)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
            }
        };

        // Set per request, never on the client: the client may be shared.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_isAzure)
        {
            request.Headers.TryAddWithoutValidation("api-key", _apiKey);
        }
        else
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
        }

        return request;
    }

    private byte[] RequestBody(LlmRequest request)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();
        writer.WriteString("model", _model);
        writer.WriteStartArray("messages");
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            WriteMessage(writer, "system", request.SystemPrompt);
        }

        foreach (var message in request.Messages)
        {
            WriteMessage(writer, message.Role == ConversationRole.Assistant ? "assistant" : "user", message.Content);
        }

        writer.WriteEndArray();

        // What each SDK sent: Azure.AI.OpenAI swapped max_completion_tokens for max_tokens.
        writer.WriteNumber(_isAzure ? "max_tokens" : "max_completion_tokens", request.MaxTokens);
        writer.WriteNumber("temperature", request.Temperature);
        writer.WriteEndObject();
        writer.Flush();

        return stream.ToArray();
    }

    private static void WriteMessage(Utf8JsonWriter writer, string role, string content)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);
        writer.WriteString("content", content);
        writer.WriteEndObject();
    }

    private Beacon.Core.Models.Ai.LlmResponse Completion(byte[] body)
    {
        using var document = Parse(body);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            throw new AiServiceException($"{DisplayName} response did not contain a completion.");
        }

        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            throw new AiServiceException($"{DisplayName} response did not contain usage information.");
        }

        var choice = choices[0];
        var inputTokens = TokenCountOf(usage, "prompt_tokens");
        var outputTokens = TokenCountOf(usage, "completion_tokens");
        var (inputCostPerMillion, outputCostPerMillion) = Pricing();
        var truncated = choice.ValueKind == JsonValueKind.Object
            && choice.TryGetProperty("finish_reason", out var finishReason)
            && finishReason.ValueKind == JsonValueKind.String
            && finishReason.GetString() == "length";

        return new Beacon.Core.Models.Ai.LlmResponse
        {
            Content = ContentOf(choice),
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Cost = (inputTokens / 1_000_000m * inputCostPerMillion) + (outputTokens / 1_000_000m * outputCostPerMillion),
            Model = _model,
            PromptCacheHit = false,
            Truncated = truncated
        };
    }

    private JsonDocument Parse(byte[] body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // The parser's message can quote the body; none of it goes into the exception.
            throw new AiServiceException($"{DisplayName} returned a response that could not be read.");
        }
    }

    private (decimal Input, decimal Output) Pricing()
    {
        if (_isAzure)
        {
            return (DefaultInputCostPerMillionTokens, DefaultOutputCostPerMillionTokens);
        }

        foreach (var (prefix, input, output) in OpenAiPricing)
        {
            if (_model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return (input, output);
            }
        }

        return (DefaultInputCostPerMillionTokens, DefaultOutputCostPerMillionTokens);
    }

    private TimeSpan? RequestedDelay(HttpResponseMessage response)
    {
        // A negative, NaN or unreadable retry-after-ms is ignored; one past the cap (infinity included) is a wait too long.
        if (response.Headers.TryGetValues("retry-after-ms", out var values)
            && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds)
            && milliseconds >= 0)
        {
            return milliseconds <= MaxRetryAfter.TotalMilliseconds ? TimeSpan.FromMilliseconds(milliseconds) : TimeSpan.MaxValue;
        }

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

    // The provider's error code when it is a plain token such as invalid_api_key; never the error message.
    private static async Task<string?> ErrorCodeAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            var body = await ReadCappedAsync(content, MaxErrorBytes, cancellationToken);
            if (body is null)
            {
                return null;
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                && code.GetString() is { } token
                && ErrorCodeToken().IsMatch(token))
            {
                return token;
            }

            return null;
        }
        catch (Exception ex) when (ex is JsonException || ((ex is IOException or HttpRequestException) && !cancellationToken.IsCancellationRequested))
        {
            // An unreadable error body only means the failure carries no error code.
            return null;
        }
    }

    private string FailureMessage(HttpStatusCode status, string? errorCode)
    {
        return errorCode is null
            ? $"{DisplayName} returned HTTP {(int)status}."
            : $"{DisplayName} returned HTTP {(int)status} ({errorCode}).";
    }

    [GeneratedRegex("^[a-z0-9_.\\-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCodeToken();

    private readonly record struct Attempt(HttpStatusCode Status, byte[]? Body, TimeSpan? RequestedDelay, string? ErrorCode)
    {
        [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(Body))]
        public bool Succeeded => Body is not null;
    }
}

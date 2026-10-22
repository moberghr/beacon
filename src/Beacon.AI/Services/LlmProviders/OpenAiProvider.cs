namespace Beacon.AI.Services.LlmProviders;

/// <summary>OpenAI chat completions, built on its own outside the provider registrations.</summary>
[Obsolete("OpenAI providers are built through the LLM provider registrations that AddBeaconAI() installs. This type will be removed in a later major version.")]
public class OpenAiProvider : ILlmProvider
{
    private readonly OpenAiCompatibleProvider _provider;

    public OpenAiProvider(string apiKey, string model = "gpt-4o-mini")
        : this(LlmHttpClients.Shared, apiKey, model)
    {
    }

    internal OpenAiProvider(HttpClient httpClient, string apiKey, string model)
    {
        _provider = OpenAiCompatibleProvider.ForOpenAi(httpClient, apiKey, model);
    }

    /// <summary>The model the client was built for.</summary>
    internal string Model => _provider.Model;

    public Task<Beacon.Core.Models.Ai.LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        return _provider.CompleteAsync(request, cancellationToken);
    }

    public Task<TokenCount> CountTokensAsync(string text, CancellationToken cancellationToken = default)
    {
        return _provider.CountTokensAsync(text, cancellationToken);
    }
}

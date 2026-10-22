namespace Beacon.AI.Services.LlmProviders;

/// <summary>Azure OpenAI chat completions, built on its own outside the provider registrations.</summary>
[Obsolete("Azure OpenAI providers are built through the LLM provider registrations that AddBeaconAI() installs. This type will be removed in a later major version.")]
public class AzureOpenAiProvider : ILlmProvider
{
    private readonly OpenAiCompatibleProvider _provider;

    public AzureOpenAiProvider(string endpoint, string apiKey, string deploymentName)
        : this(LlmHttpClients.Shared, endpoint, apiKey, deploymentName)
    {
    }

    internal AzureOpenAiProvider(HttpClient httpClient, string endpoint, string apiKey, string deploymentName)
    {
        _provider = OpenAiCompatibleProvider.ForAzure(httpClient, endpoint, apiKey, deploymentName);
    }

    /// <summary>The deployment the client was built for.</summary>
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
